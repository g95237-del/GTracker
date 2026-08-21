using System.Globalization;
using System.Text;
using GTracker.Core.Projects;

namespace GTracker.Core.Godot;

public sealed record GodotRuntimeMapping(
    UnityTriggerKind Kind,
    string Candidate,
    string ActionName,
    string ObjectPath,
    int? CycleDurationMilliseconds,
    bool IsReaction,
    string SceneName = "",
    bool ActionLoops = false);

public sealed record GodotRuntimeConfiguration(string EdiBaseUrl, IReadOnlyList<GodotRuntimeMapping> Mappings);

internal static class GodotDiscoveryScript
{
    public static string Create(uint engineMajorVersion, GodotRuntimeConfiguration? runtime = null)
    {
        if (engineMajorVersion != 3)
            throw new NotSupportedException("Godot discovery installation currently supports Godot 3 exports only.");
        var baseUrl = runtime is null ? string.Empty : NormalizeBaseUrl(runtime.EdiBaseUrl);
        var mappings = runtime?.Mappings ?? [];
        var scenes = mappings.Where(mapping => mapping.Kind == UnityTriggerKind.Scene)
            .GroupBy(mapping => Normalize(mapping.Candidate), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .Select(group => group.Last())
            .Select(mapping => $"{Literal(Normalize(mapping.Candidate))}: {Literal(mapping.ActionName)}");
        var animations = mappings.Where(mapping => mapping.Kind == UnityTriggerKind.AnimationClip &&
                                                   !string.IsNullOrWhiteSpace(mapping.Candidate) &&
                                                   !string.IsNullOrWhiteSpace(mapping.ActionName))
            .Select(mapping => "{" + string.Join(", ",
                $"\"candidate\": {Literal(Normalize(mapping.Candidate))}",
                $"\"path\": {Literal(mapping.ObjectPath.Trim('/'))}",
                $"\"duration\": {mapping.CycleDurationMilliseconds ?? 0}",
                $"\"action\": {Literal(mapping.ActionName)}",
                $"\"reaction\": {mapping.IsReaction.ToString().ToLowerInvariant()}",
                $"\"action_loop\": {mapping.ActionLoops.ToString().ToLowerInvariant()}",
                $"\"scene\": {Literal(Normalize(mapping.SceneName))}") + "}");
        return Template
            .Replace("__EDI_BASE_URL__", Literal(baseUrl), StringComparison.Ordinal)
            .Replace("__SCENE_MAPPINGS__", "{" + string.Join(", ", scenes) + "}", StringComparison.Ordinal)
            .Replace("__ANIMATION_MAPPINGS__", "[" + string.Join(", ", animations) + "]", StringComparison.Ordinal);
    }

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("EDI base URL must be an absolute HTTP or HTTPS URL.", nameof(value));
        return value.Trim().TrimEnd('/');
    }

    private static string Normalize(string value) => new(value.ToLowerInvariant()
        .Where(character => character is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());

    private static string Literal(string value)
    {
        var output = new StringBuilder("\"");
        foreach (var character in value)
            output.Append(character switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => character.ToString() });
        return output.Append('"').ToString();
    }

    private const string Template = """
        extends Node

        const POLL_SECONDS = 0.10
        const UPDATE_SECONDS = 0.25
        const TELEMETRY_RELATIVE_PATH = "GTrackerRuntime/Godot/telemetry.tsv"
        const EDI_BASE_URL = __EDI_BASE_URL__

        var _elapsed = 0.0
        var _update_elapsed = 0.0
        var _scene_id = 0
        var _scene_name = ""
        var _players = {}
        var _states = {}
        var _telemetry_path = ""
        var _scene_mappings = __SCENE_MAPPINGS__
        var _animation_mappings = __ANIMATION_MAPPINGS__
        var _active_owner = 0
        var _active_action = ""
        var _runtime_states = {}
        var _runtime_sequence = 0
        var _http = null
        var _edi_queue = []
        var _edi_busy = false


        func _ready():
            pause_mode = Node.PAUSE_MODE_PROCESS
            var game_root = OS.get_executable_path().get_base_dir()
            _telemetry_path = game_root.plus_file(TELEMETRY_RELATIVE_PATH)
            get_tree().connect("node_added", self, "_on_node_added")
            _index_node(get_tree().root)
            if EDI_BASE_URL != "":
                _http = HTTPRequest.new()
                _http.timeout = 2.0
                add_child(_http)
                _http.connect("request_completed", self, "_on_edi_completed")
            _emit("SESSION", "", "", "observer-started", "engine=godot3")
            _emit("CAPABILITY", "", "", "AnimationPlayer", "support=runtime-observer+edi")
            call_deferred("_poll_scene")


        func _exit_tree():
            if EDI_BASE_URL != "":
                _queue_post("/Stop")
            _emit("SESSION", _scene_name, "", "observer-stopped", "")


        func _process(delta):
            _elapsed += delta
            _update_elapsed += delta
            if _elapsed < POLL_SECONDS:
                return
            _elapsed = 0.0
            _poll_scene()
            _poll_players(_update_elapsed >= UPDATE_SECONDS)
            if _update_elapsed >= UPDATE_SECONDS:
                _update_elapsed = 0.0


        func _on_node_added(node):
            _index_node(node)


        func _index_node(node):
            if node is AnimationPlayer:
                _players[node.get_instance_id()] = weakref(node)
            for child in node.get_children():
                _index_node(child)


        func _poll_scene():
            var scene = get_tree().current_scene
            var current_id = scene.get_instance_id() if scene != null else 0
            if current_id == _scene_id:
                return
            var previous = _scene_name
            _scene_id = current_id
            _scene_name = _scene_label(scene)
            _states.clear()
            _active_owner = 0
            _runtime_states.clear()
            _emit("SCENE", _scene_name, "", _scene_name, "from=" + previous)
            _activate_fallback()


        func _poll_players(emit_update):
            var stale = []
            for id in _players.keys():
                var player = _players[id].get_ref()
                if player == null or not is_instance_valid(player):
                    stale.append(id)
                    continue
                var playing = player.is_playing()
                var animation = player.current_animation
                if animation == "":
                    animation = player.assigned_animation
                var position = player.current_animation_position
                var length = player.current_animation_length
                var path = str(player.get_path())
                var loop = false
                if animation != "" and player.has_animation(animation):
                    loop = player.get_animation(animation).loop
                var previous = _states.get(id, {})
                var was_playing = previous.get("playing", false)
                var previous_animation = previous.get("animation", "")
                var previous_position = previous.get("position", 0.0)
                var effective_speed = player.get_playing_speed()
                if playing and (not was_playing or previous_animation != animation):
                    _emit("ANIMATION_START", _scene_name, path, animation, _timing_details(position, length, effective_speed, loop))
                    _activate_animation(id, animation, path, length, position, effective_speed)
                elif playing and previous_animation == animation and _wrapped(previous_position, position, length, effective_speed):
                    var wrap_kind = "ANIMATION_LOOP" if loop else "ANIMATION_RESTART"
                    _emit(wrap_kind, _scene_name, path, animation, _timing_details(position, length, effective_speed, true))
                    _activate_animation(id, animation, path, length, position, effective_speed, true)
                elif not playing and was_playing:
                    _emit("ANIMATION_STOP", _scene_name, path, previous_animation, _timing_details(previous_position, previous.get("length", 0.0), previous.get("speed", 0.0), previous.get("loop", false)))
                    _deactivate_animation(id)
                elif playing and emit_update:
                    _emit("ANIMATION_UPDATE", _scene_name, path, animation, _timing_details(position, length, effective_speed, loop))
                    if _active_owner == 0:
                        _activate_animation(id, animation, path, length, position, effective_speed)
                    else:
                        _update_animation_state(id, animation, path, length, position, effective_speed)
                _states[id] = {"playing": playing, "animation": animation, "position": position, "length": length, "speed": effective_speed, "loop": loop}
            for id in stale:
                _players.erase(id)
                _states.erase(id)
                _deactivate_animation(id)


        func _activate_animation(id, animation, path, length, position, speed, wrapped = false):
            var rate = max(0.000001, abs(speed))
            var duration = int(round(length * 1000.0 / rate))
            var phase = position / rate if speed >= 0.0 else (length - position) / rate
            var mapping = _match_animation(animation, path, duration)
            if mapping.empty():
                _runtime_states.erase(id)
                if _active_owner == id:
                    _active_owner = 0
                    _resume_runtime()
                return
            var already_active = _active_owner == id and _runtime_states.has(id) and _runtime_states[id].action == mapping.action
            _runtime_sequence += 1
            _runtime_states[id] = {"action": mapping.action, "reaction": mapping.reaction, "action_loop": mapping.action_loop, "seek": int(round(max(0.0, phase) * 1000.0)), "sequence": _runtime_sequence}
            if _active_owner != 0 and _runtime_states.has(_active_owner) and _runtime_states[_active_owner].reaction and not mapping.reaction:
                return
            _active_owner = id
            if wrapped and already_active and mapping.action_loop:
                return
            _play(mapping.action, _runtime_states[id].seek, "reaction" if mapping.reaction else "animation")


        func _update_animation_state(id, animation, path, length, position, speed):
            var rate = max(0.000001, abs(speed))
            var duration = int(round(length * 1000.0 / rate))
            var phase = position / rate if speed >= 0.0 else (length - position) / rate
            var mapping = _match_animation(animation, path, duration)
            if mapping.empty():
                _runtime_states.erase(id)
                if _active_owner == id:
                    _active_owner = 0
                    _resume_runtime()
                return
            if not _runtime_states.has(id):
                _runtime_sequence += 1
            var sequence = _runtime_states[id].sequence if _runtime_states.has(id) else _runtime_sequence
            _runtime_states[id] = {"action": mapping.action, "reaction": mapping.reaction, "action_loop": mapping.action_loop, "seek": int(round(max(0.0, phase) * 1000.0)), "sequence": sequence}


        func _deactivate_animation(id):
            var was_owner = _active_owner == id
            var was_reaction = _runtime_states.has(id) and _runtime_states[id].reaction
            _runtime_states.erase(id)
            if not was_owner:
                return
            _active_owner = 0
            if was_reaction:
                _queue_post("/Stop")
                _emit("SCRIPT_STOP", _scene_name, "", _active_action, "source=reaction")
            _resume_runtime()


        func _resume_runtime():
            var best_id = 0
            var best_sequence = -1
            for id in _runtime_states.keys():
                var state = _runtime_states[id]
                if not state.reaction and state.sequence > best_sequence:
                    best_id = id
                    best_sequence = state.sequence
            if best_id != 0:
                _active_owner = best_id
                var state = _runtime_states[best_id]
                _play(state.action, state.seek, "animation-resume")
            else:
                _activate_fallback()


        func _activate_fallback():
            var action = _scene_mappings.get(_normalize(_scene_name), "")
            if action != "":
                _play(action, 0, "scene")
            else:
                _play("filler", 0, "filler")


        func _match_animation(candidate, object_path, duration):
            var normalized = _normalize(candidate)
            var path = str(object_path).trim_prefix("/").trim_suffix("/")
            var best = {}
            var best_score = -1
            var best_distance = 2147483647
            for mapping in _animation_mappings:
                if mapping.candidate != normalized:
                    continue
                if mapping.scene != "" and mapping.scene != _normalize(_scene_name):
                    continue
                var mapped_path = mapping.path
                if mapped_path != "" and mapped_path != path and not path.ends_with("/" + mapped_path):
                    continue
                if mapping.duration > 0 and abs(mapping.duration - duration) > max(25, int(mapping.duration / 10)):
                    continue
                var score = (4 if mapping.scene != "" else 0) + (2 if mapped_path != "" else 0) + (1 if mapping.duration > 0 else 0)
                var distance = abs(mapping.duration - duration) if mapping.duration > 0 else 2147483647
                if score > best_score or (score == best_score and distance < best_distance):
                    best = mapping
                    best_score = score
                    best_distance = distance
            return best


        func _play(action, seek, source):
            if EDI_BASE_URL == "":
                return
            _active_action = action
            _queue_play("/Play/" + action.http_escape() + "?seek=" + str(max(0, seek)))
            _emit("SCRIPT_PLAY", _scene_name, "", action, "source=" + source + ";seek=" + str(max(0, seek)))


        func _queue_post(route):
            if _http == null:
                return
            _edi_queue.append(route)
            _dispatch_edi()


        func _queue_play(route):
            for index in range(_edi_queue.size() - 1, -1, -1):
                if str(_edi_queue[index]).begins_with("/Play/"):
                    _edi_queue.remove(index)
            _queue_post(route)


        func _dispatch_edi():
            if _edi_busy or _edi_queue.empty():
                return
            _edi_busy = true
            var route = _edi_queue.pop_front()
            var error = _http.request(EDI_BASE_URL + route, [], true, HTTPClient.METHOD_POST, "")
            if error != OK:
                _edi_busy = false
                _emit("EDI_ERROR", _scene_name, "", str(error), "route=" + route)
                call_deferred("_dispatch_edi")


        func _on_edi_completed(result, response_code, headers, body):
            _edi_busy = false
            if result != HTTPRequest.RESULT_SUCCESS or response_code < 200 or response_code >= 300:
                _emit("EDI_ERROR", _scene_name, "", str(response_code), "result=" + str(result))
            _dispatch_edi()


        func _scene_label(scene):
            if scene == null:
                return ""
            if scene.filename != "":
                return scene.filename
            return str(scene.name)


        func _timing_details(position, length, speed, loop):
            var rate = max(0.000001, abs(speed))
            var phase = position / rate if speed >= 0.0 else (length - position) / rate
            return "phaseSeconds=%.6f;cycleDurationSeconds=%.6f;speed=%.6f;loop=%s" % [phase, length / rate, speed, str(loop).to_lower()]


        func _wrapped(previous_position, position, length, speed):
            if length <= 0.0:
                return false
            if speed >= 0.0:
                return previous_position > length * 0.75 and position < length * 0.25
            return previous_position < length * 0.25 and position > length * 0.75


        func _normalize(value):
            var output = ""
            for character in str(value).to_lower():
                if character in "abcdefghijklmnopqrstuvwxyz0123456789":
                    output += character
            return output


        func _emit(kind, scene, object_path, candidate, details):
            var file = File.new()
            var error = file.open(_telemetry_path, File.READ_WRITE)
            if error != OK:
                push_error("GTracker telemetry open failed: %s" % error)
                return
            file.seek_end()
            file.store_line("%s\t%s\t%s\t%s\t%s\t%s" % [_timestamp(), _clean(kind), _clean(scene), _clean(object_path), _clean(candidate), _clean(details)])
            file.close()


        func _timestamp():
            var milliseconds = OS.get_system_time_msecs()
            var value = OS.get_datetime_from_unix_time(int(milliseconds / 1000))
            return "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ" % [value.year, value.month, value.day, value.hour, value.minute, value.second, milliseconds % 1000]


        func _clean(value):
            return str(value).replace("\t", " ").replace("\r", " ").replace("\n", " ")
        """;
}
