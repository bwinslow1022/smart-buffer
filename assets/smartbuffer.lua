-- sorts clips by game, plays a sound, takes commands from the overlay
local obs = obslua
local ffi = require("ffi")
local bit = require("bit")

ffi.cdef [[
typedef struct { long left, top, right, bottom; } SMARTBUFFER_RECT;
typedef struct { unsigned long size; SMARTBUFFER_RECT monitor; SMARTBUFFER_RECT work; unsigned long flags; } SMARTBUFFER_MONITORINFO;
typedef struct { unsigned long size; SMARTBUFFER_RECT monitor; SMARTBUFFER_RECT work; unsigned long flags; wchar_t device[32]; } SMARTBUFFER_MONITORINFOEX;
typedef struct { unsigned long size; wchar_t name[32]; wchar_t text[128]; unsigned long flags; wchar_t id[128]; wchar_t key[128]; } SMARTBUFFER_DISPLAY_DEVICE;
int EnumDisplayDevicesW(const wchar_t *, unsigned long, SMARTBUFFER_DISPLAY_DEVICE *, unsigned long);
typedef int (*SMARTBUFFER_ENUM)(void *, intptr_t);
int MultiByteToWideChar(unsigned int, unsigned long, const char *, int, wchar_t *, int);
int WideCharToMultiByte(unsigned int, unsigned long, const wchar_t *, int, char *, int, const char *, int *);
int PlaySoundW(const wchar_t *, void *, unsigned long);
void *GetForegroundWindow(void);
int InternalGetWindowText(void *, wchar_t *, int);
int GetClassNameW(void *, wchar_t *, int);
int GetWindowRect(void *, SMARTBUFFER_RECT *);
void *MonitorFromWindow(void *, unsigned long);
int GetMonitorInfoW(void *, SMARTBUFFER_MONITORINFO *);
int EnumWindows(SMARTBUFFER_ENUM, intptr_t);
unsigned long GetWindowThreadProcessId(void *, unsigned long *);
unsigned long GetCurrentProcessId(void);
int PostMessageW(void *, unsigned int, uintptr_t, intptr_t);
int IsWindowVisible(void *);
]]

local winmm = ffi.load("winmm")

local CP_UTF8 = 65001
local SND_FILE_ASYNC = 0x20003
local MONITOR_NEAREST = 2
local WM_CLOSE = 0x0010
local SCENE = "Clips"
local GAME_SOURCE = "Game"
local DESKTOP_SOURCE = "Desktop"
local DESKTOP_FOLDER = "Desktop"
local COMMAND_MS = 200
local QUIT_RETRY_MS = 1000
local QUIT_POPUP_TRIES = 5
local MIC_CHANNEL = 3
local RECORD_PATH_WAIT_MS = 10000
local MOVE_WAIT_MS = 120000
local OUTPUT_SECTION = "AdvOut"
local OUTPUT_PATH_KEY = "RecFilePath"
local MONITOR_MS = 1000
local MONITOR_ACTIVE = "active"
local ATTACHED_TO_DESKTOP = 0x1
local PRIMARY_DEVICE = 0x4
local GET_DEVICE_INTERFACE_NAME = 0x1

-- fullscreen capture also grabs browsers and apps, their window titles make junk folders
local NOT_GAMES = {
    ["chrome"] = true, ["msedge"] = true, ["firefox"] = true, ["brave"] = true, ["opera"] = true,
    ["opera_gx"] = true, ["vivaldi"] = true, ["arc"] = true, ["iexplore"] = true,
    ["discord"] = true, ["spotify"] = true, ["code"] = true, ["claude"] = true, ["slack"] = true,
    ["teams"] = true, ["ms-teams"] = true, ["explorer"] = true, ["notepad"] = true,
    ["windowsterminal"] = true, ["cmd"] = true, ["powershell"] = true, ["pwsh"] = true,
    ["vlc"] = true, ["mpv"] = true, ["obs64"] = true, ["steamwebhelper"] = true,
    ["smart buffer"] = true, ["smartbuffer"] = true,
}
-- the same apps by window class, for desktop capture where only the window is known
-- (chrome, edge and every electron app like discord, spotify and vs code share the first one)
local NOT_GAME_CLASSES = {
    ["Chrome_WidgetWin_1"] = true, ["MozillaWindowClass"] = true, ["CabinetWClass"] = true,
    ["ConsoleWindowClass"] = true, ["CASCADIA_HOSTING_WINDOW_CLASS"] = true,
    ["ApplicationFrameWindow"] = true, ["Notepad"] = true, ["mpv"] = true,
}

local capture_game = true
local sort_by_game = true
local save_sound = true
local recordings_dir = ""
local monitor_choice = ""
local monitor_wait = 0
local applied_monitor = nil
local applied_layout = nil

local game = nil
local connected = false
local desktop_visible = true
local applied_visible = nil
local status_dirty = true
local quitting = false
local quit_wait = 0
local quit_tries = 0
local sound_file = nil
local command_file = nil
local status_file = nil
local games_file = nil
local sources_file = nil
local record_started = 0
local record_game = nil
local shot_game = nil
local clips_path = nil
local record_path_wait = 0
local last_mic = nil
local moves = {}
local saved_count = 0
local saved_kind = ""
local saved_game = ""
local saved_path = ""

local function wide(text)
    local size = ffi.C.MultiByteToWideChar(CP_UTF8, 0, text, -1, nil, 0)
    local buffer = ffi.new("wchar_t[?]", size)
    ffi.C.MultiByteToWideChar(CP_UTF8, 0, text, -1, buffer, size)
    return buffer
end

local function narrow(buffer, length)
    if length <= 0 then
        return ""
    end
    local size = ffi.C.WideCharToMultiByte(CP_UTF8, 0, buffer, length, nil, 0, nil, nil)
    local text = ffi.new("char[?]", size)
    ffi.C.WideCharToMultiByte(CP_UTF8, 0, buffer, length, text, size, nil, nil)
    return ffi.string(text, size)
end

-- this one never messages the window, so it can't deadlock against a closing obs
local function window_title(window)
    local buffer = ffi.new("wchar_t[256]")
    return narrow(buffer, ffi.C.InternalGetWindowText(window, buffer, 256))
end

local function window_class(window)
    local buffer = ffi.new("wchar_t[256]")
    return narrow(buffer, ffi.C.GetClassNameW(window, buffer, 256))
end

local function is_app_exe(exe)
    return NOT_GAMES[exe:lower():gsub("%.exe$", "")] == true
end

local function clean_name(name)
    name = name:gsub('[<>:"/\\|?*%c]', "")
    name = name:gsub("^[%s%.]+", "")
    name = name:gsub("[%s%.]+$", "")
    if #name > 80 then
        name = name:sub(1, 80):gsub("[\128-\255]+$", "")
    end
    if name == "" then
        return nil
    end
    return name
end

-- titles like "game v1.2.3 id=abc" change every launch so i use the exe for those
local function game_name(title, exe)
    local short = title:match("^(.-)%s+[-|:]%s") or title
    if short ~= "" and not short:find("%d+%.%d+") then
        local name = clean_name(short)
        if name then
            return name
        end
    end
    return clean_name((exe:gsub("%.[eE][xX][eE]$", "")))
end

-- names the user marked as games from the player, they win over every other rule
local function marked_games()
    local set = {}
    local text = obs.os_file_exists(games_file) and obs.os_quick_read_utf8_file(games_file) or ""
    for line in text:gmatch("[^\r\n]+") do
        set[line] = true
    end
    return set
end

-- whatever is in front, game or not, so a desktop clip can later be sorted with one click
local function foreground_name()
    local window = ffi.C.GetForegroundWindow()
    if window == nil then
        return nil
    end
    local title = window_title(window)
    if title == "" or title == "Program Manager" or title:match("^OBS %d") then
        return nil
    end
    return game_name(title, "")
end

-- only reads the window title so desktop capture never opens the game process
local function foreground_game()
    local window = ffi.C.GetForegroundWindow()
    if window == nil then
        return nil
    end
    local name = foreground_name()
    if name ~= nil and marked_games()[name] then
        return name
    end
    local rect = ffi.new("SMARTBUFFER_RECT")
    local info = ffi.new("SMARTBUFFER_MONITORINFO")
    info.size = ffi.sizeof(info)
    local monitor = ffi.C.MonitorFromWindow(window, MONITOR_NEAREST)
    if ffi.C.GetWindowRect(window, rect) == 0 or ffi.C.GetMonitorInfoW(monitor, info) == 0 then
        return nil
    end
    local fills = rect.left <= info.monitor.left and rect.top <= info.monitor.top
        and rect.right >= info.monitor.right and rect.bottom >= info.monitor.bottom
    if not fills then
        return nil
    end
    local title = window_title(window)
    if title == "Program Manager" or NOT_GAME_CLASSES[window_class(window)] then
        return nil
    end
    return game_name(title, "")
end

local function wide_string(chars, max)
    local length = 0
    while length < max and chars[length] ~= 0 do
        length = length + 1
    end
    return narrow(chars, length)
end

-- the same id obs's monitor capture uses, like \\?\DISPLAY#AOCB454#...
local function monitor_id(adapter_name)
    local device = ffi.new("SMARTBUFFER_DISPLAY_DEVICE")
    device.size = ffi.sizeof(device)
    if ffi.C.EnumDisplayDevicesW(adapter_name, 0, device, GET_DEVICE_INTERFACE_NAME) == 0 then
        return nil
    end
    return wide_string(device.id, 128)
end

-- every connected monitor's id, and which one windows calls main
local function monitors()
    local ids, primary = {}, nil
    local adapter = ffi.new("SMARTBUFFER_DISPLAY_DEVICE")
    local index = 0
    while true do
        adapter.size = ffi.sizeof(adapter)
        if ffi.C.EnumDisplayDevicesW(nil, index, adapter, 0) == 0 then
            break
        end
        if bit.band(adapter.flags, ATTACHED_TO_DESKTOP) ~= 0 then
            local id = monitor_id(adapter.name)
            if id ~= nil and id ~= "" then
                ids[#ids + 1] = id
                if bit.band(adapter.flags, PRIMARY_DEVICE) ~= 0 then
                    primary = id
                end
            end
        end
        index = index + 1
    end
    return ids, primary
end

local function active_monitor()
    local window = ffi.C.GetForegroundWindow()
    if window == nil then
        return nil
    end
    -- clicking the desktop, the taskbar, obs or smart buffer itself doesn't move the capture
    local title = window_title(window)
    if title == "" or title == "Program Manager" or title == "Smart Buffer" or title:match("^OBS %d") then
        return nil
    end
    local info = ffi.new("SMARTBUFFER_MONITORINFOEX")
    info.size = ffi.sizeof(info)
    local monitor = ffi.C.MonitorFromWindow(window, MONITOR_NEAREST)
    if ffi.C.GetMonitorInfoW(monitor, ffi.cast("SMARTBUFFER_MONITORINFO *", info)) == 0 then
        return nil
    end
    return monitor_id(info.device)
end

-- obs falls back to the first monitor when it starts while the screens are asleep and never
-- goes back, so the desktop source is pointed again whenever the choice or the monitors change
local function follow_monitor()
    local ids, primary = monitors()
    local wanted = monitor_choice
    if monitor_choice == "" then
        wanted = primary
    elseif monitor_choice == MONITOR_ACTIVE then
        wanted = active_monitor() or applied_monitor or primary
    end
    local layout = table.concat(ids, "|")
    local present = false
    for _, id in ipairs(ids) do
        present = present or id == wanted
    end
    if not present or (wanted == applied_monitor and layout == applied_layout) then
        return
    end
    local source = obs.obs_get_source_by_name(DESKTOP_SOURCE)
    if source == nil then
        return
    end
    local data = obs.obs_data_create()
    obs.obs_data_set_string(data, "monitor_id", wanted)
    obs.obs_source_update(source, data)
    obs.obs_data_release(data)
    obs.obs_source_release(source)
    applied_monitor = wanted
    applied_layout = layout
end

local function mic_state()
    local source = obs.obs_get_output_source(MIC_CHANNEL)
    if source == nil then
        return "none"
    end
    local muted = obs.obs_source_muted(source)
    obs.obs_source_release(source)
    return muted and "off" or "on"
end

local function write_status()
    local replay = obs.obs_frontend_replay_buffer_active() and "on" or "off"
    local recording = obs.obs_frontend_recording_active() and "on" or "off"
    last_mic = mic_state()
    local text = "script=on\nreplay=" .. replay
        .. "\nrecording=" .. recording
        .. "\nrecordstart=" .. record_started
        .. "\nmic=" .. last_mic
        .. "\ngame=" .. (game or "")
        .. "\nsaved=" .. saved_count
        .. "\nsavedkind=" .. saved_kind
        .. "\nsavedgame=" .. saved_game
        .. "\nsavedpath=" .. saved_path .. "\n"
    obs.os_quick_write_utf8_file(status_file, text, #text, false)
end

local function on_hooked(data)
    local title = obs.calldata_string(data, "title") or ""
    local exe = obs.calldata_string(data, "executable") or ""
    -- still captured, it just isn't a game, so its clips go to Desktop
    game = not is_app_exe(exe) and game_name(title, exe) or nil
    desktop_visible = false
    status_dirty = true
end

local function on_unhooked(data)
    game = nil
    desktop_visible = true
    status_dirty = true
end

local function connect_game_source()
    local source = obs.obs_get_source_by_name(GAME_SOURCE)
    if source == nil then
        return false
    end
    local handler = obs.obs_source_get_signal_handler(source)
    obs.signal_handler_connect(handler, "hooked", on_hooked)
    obs.signal_handler_connect(handler, "unhooked", on_unhooked)
    obs.obs_source_release(source)
    return true
end

local function apply_desktop_visible()
    local source = obs.obs_get_source_by_name(SCENE)
    if source == nil then
        return false
    end
    local scene = obs.obs_scene_from_source(source)
    local item = obs.obs_scene_find_source(scene, DESKTOP_SOURCE)
    if item ~= nil then
        obs.obs_sceneitem_set_visible(item, desktop_visible)
    end
    obs.obs_source_release(source)
    return true
end

local function move_into_game_folder(path, folder, prefix)
    if not sort_by_game or path == nil or path == "" then
        return
    end
    local dir, name = path:match("^(.*)[/\\]([^/\\]+)$")
    if dir == nil then
        return
    end
    local stamp = name:gsub("^" .. prefix .. "%s*", "")
    local target_dir = dir .. "/" .. folder
    obs.os_mkdirs(target_dir)
    local target = target_dir .. "/" .. folder .. " " .. stamp
    moves[#moves + 1] = { path = path, target = target, wait = 0 }
    return target
end

-- quick write can't append, so the file is rewritten, keeping only the newest 500 lines
local function add_line(file, line)
    local lines = {}
    local old = obs.os_file_exists(file) and obs.os_quick_read_utf8_file(file) or ""
    for existing in old:gmatch("[^\r\n]+") do
        lines[#lines + 1] = existing
    end
    lines[#lines + 1] = line
    local text = table.concat(lines, "\n", math.max(1, #lines - 499)) .. "\n"
    obs.os_quick_write_utf8_file(file, text, #text, false)
end

local function file_name(path)
    return (path or ""):match("([^/\\]+)$")
end

-- remembers which window a desktop clip came from, the player offers to sort it as that game
local function note_desktop_source(target)
    local name = foreground_name()
    if target == nil or name == nil then
        return
    end
    add_line(sources_file, file_name(target) .. "\t" .. name)
end


-- obs says a recording stopped while it's still finishing the file, so this keeps trying
local function run_moves()
    for index = #moves, 1, -1 do
        local move = moves[index]
        move.wait = move.wait + COMMAND_MS
        if obs.os_rename(move.path, move.target) == 0 then
            table.remove(moves, index)
        elseif move.wait >= MOVE_WAIT_MS then
            obs.script_log(obs.LOG_WARNING, "couldn't move file to " .. move.target)
            table.remove(moves, index)
        end
    end
end

local function play_sound()
    if save_sound then
        winmm.PlaySoundW(wide(sound_file), nil, SND_FILE_ASYNC)
    end
end

local function restore_clips_path()
    if clips_path ~= nil then
        obs.config_set_string(obs.obs_frontend_get_profile_config(), OUTPUT_SECTION, OUTPUT_PATH_KEY, clips_path)
        clips_path = nil
    end
end

-- replays and recordings share one obs path, so i only point it at recordings while one starts
local function start_recording()
    if obs.obs_frontend_recording_active() then
        return
    end
    record_game = game or foreground_game()
    if recordings_dir ~= "" and clips_path == nil then
        local config = obs.obs_frontend_get_profile_config()
        clips_path = obs.config_get_string(config, OUTPUT_SECTION, OUTPUT_PATH_KEY)
        obs.os_mkdirs(recordings_dir)
        obs.config_set_string(config, OUTPUT_SECTION, OUTPUT_PATH_KEY, recordings_dir)
        record_path_wait = 0
    end
    obs.obs_frontend_recording_start()
end

local function set_mic(on)
    local source = obs.obs_get_output_source(MIC_CHANNEL)
    if source ~= nil then
        obs.obs_source_set_muted(source, not on)
        obs.obs_source_release(source)
    end
    status_dirty = true
end

-- path is where the file ends up, so a click on the saved card can open it
local function note_saved(kind, folder, path)
    saved_count = saved_count + 1
    saved_kind = kind
    saved_game = folder or ""
    saved_path = (path or ""):gsub("\\", "/")
    -- written straight away instead of on the next timer tick, so the saved card shows sooner
    write_status()
    status_dirty = false
end

local function on_event(event)
    if event == obs.OBS_FRONTEND_EVENT_REPLAY_BUFFER_SAVED then
        local folder = game or foreground_game() or DESKTOP_FOLDER
        local last = obs.obs_frontend_get_last_replay()
        local target = move_into_game_folder(last, folder, "Replay")
        if folder == DESKTOP_FOLDER then
            note_desktop_source(target)
        end
        note_saved("clip", folder, target or last)
        play_sound()
    elseif event == obs.OBS_FRONTEND_EVENT_REPLAY_BUFFER_STARTED
        or event == obs.OBS_FRONTEND_EVENT_REPLAY_BUFFER_STOPPED then
        status_dirty = true
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STARTED then
        restore_clips_path()
        record_started = os.time()
        status_dirty = true
        play_sound()
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STOPPED then
        restore_clips_path()
        local folder = record_game or game or foreground_game() or DESKTOP_FOLDER
        local last = obs.obs_frontend_get_last_recording()
        local target = move_into_game_folder(last, folder, "") or last
        note_saved("recording", folder, target)
        record_started = 0
        record_game = nil
        status_dirty = true
        play_sound()
    elseif event == obs.OBS_FRONTEND_EVENT_SCREENSHOT_TAKEN then
        local last = obs.obs_frontend_get_last_screenshot()
        note_saved("screenshot", shot_game or DESKTOP_FOLDER, move_into_game_folder(last, shot_game or DESKTOP_FOLDER, "Screenshot") or last)
        shot_game = nil
        play_sound()
    end
end

-- a hidden tray window can't be closed from a non admin app, so obs closes itself
-- popups go first, closing the main window while one is up can hang obs on its way out
local function quit_obs()
    local me = ffi.C.GetCurrentProcessId()
    local main = {}
    local popups = {}
    local callback = ffi.cast("SMARTBUFFER_ENUM", function(window, param)
        local id = ffi.new("unsigned long[1]")
        ffi.C.GetWindowThreadProcessId(window, id)
        if id[0] == me then
            local title = window_title(window)
            if title:match("^OBS %d") then
                main[#main + 1] = window
            elseif title ~= "" and ffi.C.IsWindowVisible(window) ~= 0 then
                popups[#popups + 1] = window
            end
        end
        return 1
    end)
    ffi.C.EnumWindows(callback, 0)
    callback:free()

    quit_tries = quit_tries + 1
    local targets = main
    if #popups > 0 and quit_tries <= QUIT_POPUP_TRIES then
        targets = popups
    end
    for _, window in ipairs(targets) do
        ffi.C.PostMessageW(window, WM_CLOSE, 0, 0)
    end
end
jit.off(quit_obs, true)

local function check_commands()
    -- the mic can also change from inside obs itself
    if status_dirty or mic_state() ~= last_mic then
        status_dirty = false
        write_status()
    end
    run_moves()
    monitor_wait = monitor_wait + COMMAND_MS
    if monitor_wait >= MONITOR_MS then
        monitor_wait = 0
        follow_monitor()
    end
    -- a recording that never starts sends no event, and replays still need their own folder back
    if clips_path ~= nil then
        record_path_wait = record_path_wait + COMMAND_MS
        if record_path_wait >= RECORD_PATH_WAIT_MS then
            restore_clips_path()
        end
    end
    -- the first close can get eaten by a popup, so i keep asking until obs is gone
    if quitting then
        quit_wait = quit_wait + COMMAND_MS
        if quit_wait >= QUIT_RETRY_MS then
            quit_wait = 0
            quit_obs()
        end
    end
    if not obs.os_file_exists(command_file) then
        return
    end
    local text = obs.os_quick_read_utf8_file(command_file)
    obs.os_unlink(command_file)
    local command = text and text:match("(%a+)")
    if command == "start" then
        obs.obs_frontend_replay_buffer_start()
    elseif command == "stop" then
        obs.obs_frontend_replay_buffer_stop()
    elseif command == "save" then
        obs.obs_frontend_replay_buffer_save()
    elseif command == "recordstart" then
        start_recording()
    elseif command == "recordstop" then
        obs.obs_frontend_recording_stop()
    elseif command == "screenshot" then
        shot_game = game or foreground_game()
        obs.obs_frontend_take_screenshot()
    elseif command == "micon" then
        set_mic(true)
    elseif command == "micoff" then
        set_mic(false)
    elseif command == "quit" then
        quitting = true
        quit_obs()
    end
end

function script_description()
    return "Sorts clips by game, plays a sound when one saves, and takes commands from the SmartBuffer overlay."
end

function script_defaults(settings)
    obs.obs_data_set_default_bool(settings, "capture_game", true)
    obs.obs_data_set_default_bool(settings, "sort_by_game", true)
    obs.obs_data_set_default_bool(settings, "save_sound", true)
    obs.obs_data_set_default_string(settings, "recordings_dir", "")
    obs.obs_data_set_default_string(settings, "monitor", "")
end

function script_load(settings)
    capture_game = obs.obs_data_get_bool(settings, "capture_game")
    sort_by_game = obs.obs_data_get_bool(settings, "sort_by_game")
    save_sound = obs.obs_data_get_bool(settings, "save_sound")
    recordings_dir = obs.obs_data_get_string(settings, "recordings_dir")
    monitor_choice = obs.obs_data_get_string(settings, "monitor")

    sound_file = script_path() .. "saved.wav"
    command_file = script_path() .. "command.txt"
    status_file = script_path() .. "status.txt"
    games_file = script_path() .. "games.txt"
    sources_file = script_path() .. "desktop-sources.txt"

    obs.obs_frontend_add_event_callback(on_event)
    obs.timer_add(check_commands, COMMAND_MS)
end

function script_unload()
    local text = "script=off\nreplay=off\nrecording=off\nrecordstart=0\nmic=none\ngame=\n"
    obs.os_quick_write_utf8_file(status_file, text, #text, false)
end

function script_tick(seconds)
    if not capture_game then
        return
    end
    if not connected then
        connected = connect_game_source()
    end
    if applied_visible ~= desktop_visible and apply_desktop_visible() then
        applied_visible = desktop_visible
    end
end
