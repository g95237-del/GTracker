namespace GTracker.Core.Godot;

internal static class GodotDiscoveryScript
{
    public static string Create(uint engineMajorVersion)
    {
        if (engineMajorVersion != 3)
            throw new NotSupportedException("Godot discovery installation currently supports Godot 3 exports only.");
        return """
            extends Node

            const POLL_SECONDS = 0.10
            const UPDATE_SECONDS = 0.25
            const TELEMETRY_RELATIVE_PATH = "GTrackerRuntime/Godot/telemetry.tsv"

            var _elapsed = 0.0
            var _update_elapsed = 0.0
            var _scene_id = 0
            var _scene_name = ""
            var _players = {}
            var _states = {}
            var _telemetry_path = ""


            func _ready():
                pause_mode = Node.PAUSE_MODE_PROCESS
                var game_root = OS.get_executable_path().get_base_dir()
                _telemetry_path = game_root.plus_file(TELEMETRY_RELATIVE_PATH)
                get_tree().connect("node_added", self, "_on_node_added")
                _index_node(get_tree().root)
                _emit("SESSION", "", "", "observer-started", "engine=godot3")
                _emit("CAPABILITY", "", "", "AnimationPlayer", "support=runtime-observer")
                call_deferred("_poll_scene")


            func _exit_tree():
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
                _emit("SCENE", _scene_name, "", _scene_name, "from=" + previous)


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
                        _emit("ANIMATION_START", _scene_name, path, animation,
                            _timing_details(position, length, effective_speed, loop))
                    elif playing and loop and previous_animation == animation and _wrapped(previous_position, position, length, effective_speed):
                        _emit("ANIMATION_LOOP", _scene_name, path, animation,
                            _timing_details(position, length, effective_speed, loop))
                    elif not playing and was_playing:
                        _emit("ANIMATION_STOP", _scene_name, path, previous_animation,
                            _timing_details(previous_position, previous.get("length", 0.0), previous.get("speed", 0.0), previous.get("loop", false)))
                    elif playing and emit_update:
                        _emit("ANIMATION_UPDATE", _scene_name, path, animation,
                            _timing_details(position, length, effective_speed, loop))
                    _states[id] = {
                        "playing": playing,
                        "animation": animation,
                        "position": position,
                        "length": length,
                        "speed": effective_speed,
                        "loop": loop
                    }
                for id in stale:
                    _players.erase(id)
                    _states.erase(id)


            func _scene_label(scene):
                if scene == null:
                    return ""
                if scene.filename != "":
                    return scene.filename
                return str(scene.name)


            func _timing_details(position, length, speed, loop):
                return "phaseSeconds=%.6f;cycleDurationSeconds=%.6f;speed=%.6f;loop=%s" % [position, length, speed, str(loop).to_lower()]


            func _wrapped(previous_position, position, length, speed):
                if length <= 0.0:
                    return false
                if speed >= 0.0:
                    return previous_position > length * 0.75 and position < length * 0.25
                return previous_position < length * 0.25 and position > length * 0.75


            func _emit(kind, scene, object_path, candidate, details):
                var file = File.new()
                var error = file.open(_telemetry_path, File.READ_WRITE)
                if error != OK:
                    push_error("GTracker telemetry open failed: %s" % error)
                    return
                file.seek_end()
                file.store_line("%s\t%s\t%s\t%s\t%s\t%s" % [
                    _timestamp(), _clean(kind), _clean(scene), _clean(object_path), _clean(candidate), _clean(details)
                ])
                file.close()


            func _timestamp():
                var value = OS.get_datetime(true)
                return "%04d-%02d-%02dT%02d:%02d:%02dZ" % [
                    value.year, value.month, value.day, value.hour, value.minute, value.second
                ]


            func _clean(value):
                return str(value).replace("\t", " ").replace("\r", " ").replace("\n", " ")
            """;
    }
}
