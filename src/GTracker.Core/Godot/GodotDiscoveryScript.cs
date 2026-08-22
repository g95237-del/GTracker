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
    bool ActionLoops = false,
    int? ActionDurationMilliseconds = null,
    bool AllowNearestDuration = false);

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
            .Select(mapping =>
            {
                var portableOwner = GetPortableGalleryOwner(mapping);
                return "{" + string.Join(", ",
                    $"\"candidate\": {Literal(Normalize(mapping.Candidate))}",
                    $"\"path\": {Literal(mapping.ObjectPath.Trim('/'))}",
                    $"\"duration\": {mapping.CycleDurationMilliseconds ?? 0}",
                    $"\"action_duration\": {mapping.ActionDurationMilliseconds ?? mapping.CycleDurationMilliseconds ?? 0}",
                    $"\"nearest_duration\": {mapping.AllowNearestDuration.ToString().ToLowerInvariant()}",
                    $"\"action\": {Literal(mapping.ActionName)}",
                    $"\"reaction\": {mapping.IsReaction.ToString().ToLowerInvariant()}",
                    $"\"action_loop\": {mapping.ActionLoops.ToString().ToLowerInvariant()}",
                    $"\"scene\": {Literal(Normalize(mapping.SceneName))}",
                    $"\"portable\": {(portableOwner.Length > 0).ToString().ToLowerInvariant()}",
                    $"\"owner\": {Literal(Normalize(portableOwner))}") + "}";
            });
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

    private static string GetPortableGalleryOwner(GodotRuntimeMapping mapping)
    {
        var sceneFile = mapping.SceneName.Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty;
        if (!Path.GetFileNameWithoutExtension(sceneFile).Equals("Gallery", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var segments = mapping.ObjectPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 2 < segments.Length; index++)
            if (segments[index].Equals("Units", StringComparison.OrdinalIgnoreCase) &&
                segments[^1].Equals("AnimationPlayer", StringComparison.OrdinalIgnoreCase))
                return segments[index + 1];
        return string.Empty;
    }

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
        const RUNTIME_UPDATE_SECONDS = 0.25
        const TELEMETRY_UPDATE_SECONDS = 1.0
        const HOTKEY_RELOAD_SECONDS = 1.0
        const TELEMETRY_RELATIVE_PATH = "GTrackerRuntime/Godot/telemetry.tsv"
        const HOTKEY_RELATIVE_PATH = "GTrackerRuntime/Godot/hotkeys.cfg"
        const EDI_BASE_URL = __EDI_BASE_URL__

        var _elapsed = 0.0
        var _runtime_update_elapsed = 0.0
        var _telemetry_update_elapsed = 0.0
        var _hotkey_reload_elapsed = 0.0
        var _scene_id = 0
        var _scene_name = ""
        var _players = {}
        var _owners = {}
        var _states = {}
        var _telemetry_path = ""
        var _hotkey_path = ""
        var _telemetry_lines = []
        var _scene_mappings = __SCENE_MAPPINGS__
        var _animation_mappings = __ANIMATION_MAPPINGS__
        var _exact_mapping_index = {}
        var _portable_mapping_index = {}
        var _active_owner = 0
        var _active_action = ""
        var _runtime_states = {}
        var _runtime_sequence = 0
        var _http = null
        var _edi_queue = []
        var _edi_busy = false
        var _hotkeys = {}
        var _hotkey_values = {}
        var _hotkey_states = {}
        var _hotkey_focused = false
        var _hotkey_load_error = OK


        func _ready():
            pause_mode = Node.PAUSE_MODE_PROCESS
            var game_root = OS.get_executable_path().get_base_dir()
            _telemetry_path = game_root.plus_file(TELEMETRY_RELATIVE_PATH)
            _hotkey_path = game_root.plus_file(HOTKEY_RELATIVE_PATH)
            _index_animation_mappings()
            get_tree().connect("node_added", self, "_on_node_added")
            _index_node(get_tree().root)
            if EDI_BASE_URL != "":
                _http = HTTPRequest.new()
                _http.timeout = 2.0
                add_child(_http)
                _http.connect("request_completed", self, "_on_edi_completed")
            _load_hotkeys()
            _hotkey_focused = OS.is_window_focused()
            _sync_hotkey_states()
            _emit("SESSION", "", "", "observer-started", "engine=godot3")
            _emit("CAPABILITY", "", "", "AnimationPlayer", "support=runtime-observer+edi+hotkeys")
            call_deferred("_poll_scene")


        func _exit_tree():
            if EDI_BASE_URL != "":
                _queue_post("/Stop")
            _emit("SESSION", _scene_name, "", "observer-stopped", "")
            _flush_telemetry()


        func _process(delta):
            _poll_hotkeys(delta)
            _elapsed += delta
            _runtime_update_elapsed += delta
            _telemetry_update_elapsed += delta
            if _elapsed < POLL_SECONDS:
                return
            _elapsed = 0.0
            _poll_scene()
            var update_runtime = _runtime_update_elapsed >= RUNTIME_UPDATE_SECONDS
            var emit_telemetry_update = _telemetry_update_elapsed >= TELEMETRY_UPDATE_SECONDS
            _poll_players(update_runtime, emit_telemetry_update)
            _flush_telemetry()
            if update_runtime:
                _runtime_update_elapsed = 0.0
            if emit_telemetry_update:
                _telemetry_update_elapsed = 0.0


        func _poll_hotkeys(delta):
            _hotkey_reload_elapsed += delta
            if _hotkey_reload_elapsed >= HOTKEY_RELOAD_SECONDS:
                _hotkey_reload_elapsed = 0.0
                if _load_hotkeys():
                    _sync_hotkey_states()
            var focused = OS.is_window_focused()
            if focused != _hotkey_focused:
                _hotkey_focused = focused
                _sync_hotkey_states()
                return
            for action in _hotkeys.keys():
                var down = _is_hotkey_down(_hotkeys[action])
                if focused and down and not _hotkey_states.get(action, false):
                    _activate_hotkey(action)
                _hotkey_states[action] = down


        func _activate_hotkey(action):
            match action:
                "Pause":
                    _queue_post("/Pause?untilResume=true")
                "Resume":
                    _queue_post("/Resume?AtCurrentTime=false")
                "Intensity40":
                    _queue_post("/Intensity/40")
                "Intensity100":
                    _queue_post("/Intensity/100")
                "ActivateFiller":
                    _play("filler", 0, "hotkey")
            _emit("HOTKEY", _scene_name, "", action, "")


        func _load_hotkeys():
            var config = ConfigFile.new()
            var error = config.load(_hotkey_path)
            if error != OK:
                if error != _hotkey_load_error:
                    _emit("HOTKEY_ERROR", _scene_name, "", "config-load", "error=" + str(error))
                _hotkey_load_error = error
                return false
            _hotkey_load_error = OK
            var defaults = {
                "Pause": "1 | NumPad1",
                "Resume": "2 | NumPad2",
                "Intensity40": "3 | NumPad3",
                "Intensity100": "4 | NumPad4",
                "ActivateFiller": "5 | NumPad5"
            }
            var loaded = {}
            var loaded_values = {}
            var changed = _hotkey_values.empty()
            for action in defaults.keys():
                var value = str(config.get_value("Hotkeys", action, defaults[action]))
                var parsed = _parse_hotkey(value)
                loaded[action] = parsed["keys"]
                loaded_values[action] = value
                var value_changed = not _hotkey_values.has(action) or _hotkey_values[action] != value
                if value_changed:
                    changed = true
                if value_changed and not parsed["invalid"].empty():
                    _emit("HOTKEY_ERROR", _scene_name, "", action, "invalid=" + PoolStringArray(parsed["invalid"]).join(","))
            _hotkeys = loaded
            _hotkey_values = loaded_values
            if changed:
                _emit("HOTKEY_CONFIG", _scene_name, "", "reloaded", "")
            return changed


        func _parse_hotkey(value):
            var keys = []
            var invalid = []
            var text = str(value).strip_edges()
            if text == "" or text.to_lower() == "none":
                return {"keys": keys, "invalid": invalid}
            for item in text.split("|", false):
                var token = str(item).strip_edges()
                var key = _parse_key(token)
                if key > 0:
                    if not key in keys:
                        keys.append(key)
                elif token != "":
                    invalid.append(token)
            return {"keys": keys, "invalid": invalid}


        func _parse_key(token):
            if token.length() == 1:
                var character = token.to_upper()
                if character in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789":
                    return character.ord_at(0)
                var punctuation = {";": KEY_SEMICOLON, "=": KEY_EQUAL, "+": KEY_EQUAL, ",": KEY_COMMA, "-": KEY_MINUS, ".": KEY_PERIOD, "/": KEY_SLASH, "`": KEY_QUOTELEFT, "~": KEY_QUOTELEFT, "[": KEY_BRACKETLEFT, "\\": KEY_BACKSLASH, "]": KEY_BRACKETRIGHT, "'": KEY_APOSTROPHE, "\"": KEY_APOSTROPHE}
                return punctuation.get(token, 0)
            var normalized = token.replace("_", "").replace("-", "").to_upper()
            if normalized.length() == 2 and normalized.begins_with("D") and normalized[1] in "0123456789":
                return normalized.ord_at(1)
            if normalized.begins_with("NUMPAD") and normalized.length() == 7 and normalized[6] in "0123456789":
                return KEY_KP_0 + int(normalized.substr(6, 1))
            if normalized.begins_with("F") and normalized.substr(1).is_valid_integer():
                var function = int(normalized.substr(1))
                if function >= 1 and function <= 16:
                    return KEY_F1 + function - 1
            var named = {
                "BACKSPACE": KEY_BACKSPACE, "TAB": KEY_TAB, "ENTER": KEY_ENTER, "RETURN": KEY_ENTER,
                "SHIFT": KEY_SHIFT, "CTRL": KEY_CONTROL, "CONTROL": KEY_CONTROL, "ALT": KEY_ALT,
                "PAUSE": KEY_PAUSE, "BREAK": KEY_PAUSE, "CAPSLOCK": KEY_CAPSLOCK, "ESC": KEY_ESCAPE,
                "ESCAPE": KEY_ESCAPE, "SPACE": KEY_SPACE, "PAGEUP": KEY_PAGEUP, "PAGEDOWN": KEY_PAGEDOWN,
                "END": KEY_END, "HOME": KEY_HOME, "LEFT": KEY_LEFT, "UP": KEY_UP, "RIGHT": KEY_RIGHT,
                "DOWN": KEY_DOWN, "PRINTSCREEN": KEY_PRINT, "INSERT": KEY_INSERT, "DELETE": KEY_DELETE,
                "NUMLOCK": KEY_NUMLOCK, "SCROLLLOCK": KEY_SCROLLLOCK, "SEMICOLON": KEY_SEMICOLON,
                "EQUALS": KEY_EQUAL, "PLUS": KEY_EQUAL, "COMMA": KEY_COMMA, "MINUS": KEY_MINUS,
                "PERIOD": KEY_PERIOD, "DOT": KEY_PERIOD, "SLASH": KEY_SLASH, "BACKTICK": KEY_QUOTELEFT,
                "TILDE": KEY_QUOTELEFT, "LEFTBRACKET": KEY_BRACKETLEFT, "BACKSLASH": KEY_BACKSLASH,
                "RIGHTBRACKET": KEY_BRACKETRIGHT, "APOSTROPHE": KEY_APOSTROPHE, "QUOTE": KEY_APOSTROPHE
            }
            return named.get(normalized, 0)


        func _is_hotkey_down(keys):
            for key in keys:
                if Input.is_key_pressed(key):
                    return true
            return false


        func _sync_hotkey_states():
            for action in _hotkeys.keys():
                _hotkey_states[action] = _is_hotkey_down(_hotkeys[action])


        func _on_node_added(node):
            _index_node(node)


        func _index_node(node):
            if node is AnimationPlayer:
                var id = node.get_instance_id()
                var resource = _owner_resource(node)
                _players[id] = weakref(node)
                _owners[id] = {"key": _owner_key(node, resource), "resource": resource}
            for child in node.get_children():
                _index_node(child)


        func _index_animation_mappings():
            for mapping in _animation_mappings:
                var exact_key = mapping.candidate + "|" + mapping.scene
                if not _exact_mapping_index.has(exact_key):
                    _exact_mapping_index[exact_key] = []
                _exact_mapping_index[exact_key].append(mapping)
                if mapping.portable:
                    var portable_key = _portable_animation_name(mapping.candidate)
                    if not _portable_mapping_index.has(portable_key):
                        _portable_mapping_index[portable_key] = []
                    _portable_mapping_index[portable_key].append(mapping)


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


        func _poll_players(update_runtime, emit_telemetry_update):
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
                var previous = _states.get(id, {})
                var was_playing = previous.get("playing", false)
                var previous_animation = previous.get("animation", "")
                var previous_position = previous.get("position", 0.0)
                var loop = previous.get("loop", false)
                if animation != previous_animation and animation != "" and player.has_animation(animation):
                    loop = player.get_animation(animation).loop
                var effective_speed = player.get_playing_speed()
                var has_event = playing and (not was_playing or previous_animation != animation) or playing and previous_animation == animation and _wrapped(previous_position, position, length, effective_speed) or not playing and was_playing or playing and update_runtime
                var path = str(player.get_path()) if has_event else ""
                var owner_details = _owner_details(id) if has_event and emit_telemetry_update else ""
                if playing and (not was_playing or previous_animation != animation):
                    _emit("ANIMATION_START", _scene_name, path, animation, _timing_details(position, length, effective_speed, loop) + _owner_details(id))
                    _activate_animation(id, animation, path, length, position, effective_speed)
                elif playing and previous_animation == animation and _wrapped(previous_position, position, length, effective_speed):
                    var wrap_kind = "ANIMATION_LOOP" if loop else "ANIMATION_RESTART"
                    _emit(wrap_kind, _scene_name, path, animation, _timing_details(position, length, effective_speed, true) + _owner_details(id))
                    _activate_animation(id, animation, path, length, position, effective_speed, true)
                elif not playing and was_playing:
                    _emit("ANIMATION_STOP", _scene_name, path, previous_animation, _timing_details(previous_position, previous.get("length", 0.0), previous.get("speed", 0.0), previous.get("loop", false)) + _owner_details(id))
                    _deactivate_animation(id)
                elif playing and update_runtime:
                    if emit_telemetry_update:
                        _emit("ANIMATION_UPDATE", _scene_name, path, animation, _timing_details(position, length, effective_speed, loop) + owner_details)
                    if _active_owner == 0:
                        _activate_animation(id, animation, path, length, position, effective_speed)
                    else:
                        _update_animation_state(id, animation, path, length, position, effective_speed)
                _states[id] = {"playing": playing, "animation": animation, "position": position, "length": length, "speed": effective_speed, "loop": loop}
            for id in stale:
                _players.erase(id)
                _owners.erase(id)
                _states.erase(id)
                _deactivate_animation(id)


        func _activate_animation(id, animation, path, length, position, speed, wrapped = false):
            var rate = max(0.000001, abs(speed))
            var duration = int(round(length * 1000.0 / rate))
            var phase = position / rate if speed >= 0.0 else (length - position) / rate
            var mapping = _match_animation(animation, path, duration, _owners.get(id, {}).get("key", ""))
            if mapping.empty():
                _runtime_states.erase(id)
                if _active_owner == id:
                    _active_owner = 0
                    _resume_runtime()
                return
            var already_active = _active_owner == id and _runtime_states.has(id) and _runtime_states[id].action == mapping.action
            _runtime_sequence += 1
            _runtime_states[id] = {"action": mapping.action, "reaction": mapping.reaction, "action_loop": mapping.action_loop, "seek": _scaled_seek(phase, duration, mapping.action_duration), "sequence": _runtime_sequence}
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
            var mapping = _match_animation(animation, path, duration, _owners.get(id, {}).get("key", ""))
            if mapping.empty():
                _runtime_states.erase(id)
                if _active_owner == id:
                    _active_owner = 0
                    _resume_runtime()
                return
            if not _runtime_states.has(id):
                _runtime_sequence += 1
            var action_changed = _runtime_states.has(id) and _runtime_states[id].action != mapping.action
            var sequence = _runtime_states[id].sequence if _runtime_states.has(id) else _runtime_sequence
            _runtime_states[id] = {"action": mapping.action, "reaction": mapping.reaction, "action_loop": mapping.action_loop, "seek": _scaled_seek(phase, duration, mapping.action_duration), "sequence": sequence}
            if _active_owner == id and action_changed:
                _play(mapping.action, _runtime_states[id].seek, "animation-speed-change")


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


        func _match_animation(candidate, object_path, duration, owner):
            var normalized = _normalize(candidate)
            var portable_candidate = _portable_animation_name(normalized)
            var path = str(object_path).trim_prefix("/").trim_suffix("/")
            var scene = _normalize(_scene_name)
            var candidates = []
            for mapping in _exact_mapping_index.get(normalized + "|" + scene, []):
                candidates.append(mapping)
            for mapping in _exact_mapping_index.get(normalized + "|", []):
                candidates.append(mapping)
            for mapping in _portable_mapping_index.get(portable_candidate, []):
                candidates.append(mapping)
            var best = {}
            var best_score = -1
            var best_distance = 2147483647
            for mapping in candidates:
                var mapped_path = mapping.path
                var scene_matches = mapping.scene == "" or mapping.scene == scene
                var path_matches = mapped_path == "" or mapped_path == path or path.ends_with("/" + mapped_path)
                var exact = mapping.candidate == normalized and scene_matches and path_matches
                var portable = mapping.portable and _portable_animation_name(mapping.candidate) == portable_candidate and _owner_matches(mapping.owner, owner)
                if not exact and not portable:
                    continue
                if mapping.duration > 0 and not mapping.nearest_duration and abs(mapping.duration - duration) > max(25, int(mapping.duration / 10)):
                    continue
                var score = (16 if exact else 4) + (4 if exact and mapping.scene != "" else 0) + (2 if exact and mapped_path != "" else 0) + (1 if mapping.duration > 0 else 0)
                var distance = abs(mapping.duration - duration) if mapping.duration > 0 else 2147483647
                if score > best_score or (score == best_score and distance < best_distance):
                    best = mapping
                    best_score = score
                    best_distance = distance
            return best


        func _portable_animation_name(value):
            var normalized = _normalize(value)
            if not normalized.begins_with("p"):
                return normalized
            var index = 1
            while index < normalized.length() and normalized[index] in "0123456789":
                index += 1
            if index > 1 and normalized.substr(index, 3) == "pre":
                return normalized.substr(0, index) + normalized.substr(index + 3)
            return normalized


        func _owner_matches(expected, actual):
            if expected == "" or actual == "":
                return false
            if expected == actual:
                return true
            if not actual.begins_with(expected):
                return false
            var suffix = actual.substr(expected.length())
            if suffix == "":
                return true
            for character in suffix:
                if not character in "0123456789":
                    return false
            return true


        func _scaled_seek(phase, observed_duration, action_duration):
            var observed_seek = max(0.0, phase) * 1000.0
            if observed_duration <= 0 or action_duration <= 0:
                return int(round(observed_seek))
            return int(round(clamp(observed_seek / observed_duration, 0.0, 1.0) * action_duration))


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


        func _owner_resource(player):
            var current = player.get_parent()
            while current != null:
                if current.filename != "":
                    return str(current.filename)
                current = current.get_parent()
            return ""


        func _owner_key(player, resource):
            if resource != "":
                return _normalize(str(resource).get_file().get_basename())
            var parent = player.get_parent()
            return _normalize(parent.name) if parent != null else ""


        func _owner_details(id):
            var owner = _owners.get(id, {})
            return ";ownerKey=" + str(owner.get("key", "")) + ";ownerResource=" + str(owner.get("resource", ""))


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
            _telemetry_lines.append("%s\t%s\t%s\t%s\t%s\t%s" % [_timestamp(), _clean(kind), _clean(scene), _clean(object_path), _clean(candidate), _clean(details)])


        func _flush_telemetry():
            if _telemetry_lines.empty():
                return
            var file = File.new()
            var error = file.open(_telemetry_path, File.READ_WRITE)
            if error != OK:
                push_error("GTracker telemetry open failed: %s" % error)
                return
            file.seek_end()
            for line in _telemetry_lines:
                file.store_line(line)
            file.close()
            _telemetry_lines.clear()


        func _timestamp():
            var milliseconds = OS.get_system_time_msecs()
            var value = OS.get_datetime_from_unix_time(int(milliseconds / 1000))
            return "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ" % [value.year, value.month, value.day, value.hour, value.minute, value.second, milliseconds % 1000]


        func _clean(value):
            return str(value).replace("\t", " ").replace("\r", " ").replace("\n", " ")
        """;
}
