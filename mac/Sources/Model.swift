import EventKit
import AppKit
import SwiftUI
import Darwin
import ServiceManagement
import CoreAudio
import IOKit.ps
import Network
import AVFoundation

// MARK: - Model

@MainActor final class Model: ObservableObject {
    @Published var items: [WatchItem] {
        didSet {
            UserDefaults.standard.set(try? JSONEncoder().encode(items), forKey: "items")
            tick()
        }
    }
    @Published var states: [ItemState] = []
    @Published var limits: [ProviderLimits] = []
    @Published var alerts: [JobAlert] = []  // failed runner jobs, stay until dismissed
    @Published var nowPlaying: NowPlaying?
    private var npBusy = false
    private var lastAudible: Set<String> = []
    @Published var toast: Toast?
    @Published var updatePending = false
    @Published var asks: [AgentAsk] = []
    /// Models loaded in LM Studio / Ollama ("lmstudio" / "ollama" → names), read every few seconds off the main thread.
    private var localModels: [String: [String]] = [:]
    private var readingLocalModels = false  // Claude Code waiting on an answer (AgentAsks.swift)
    @Published var battery: BatteryState?
    @Published var thermals: [ThermalReading] = []
    @Published var fans: [FanReading] = []
    let fanControl = FanControl()
    @Published var events: [CalEvent] = []  // today's all-day ones first, then timed ones by start
    /// Events the user closed on the notch; kept until they leave the calendar's window.
    @Published var dismissedEvents = Set(UserDefaults.standard.stringArray(forKey: "dismissedEvents") ?? []) {
        didSet { UserDefaults.standard.set(Array(dismissedEvents), forKey: "dismissedEvents") }
    }
    var visibleEvents: [CalEvent] { events.filter { !dismissedEvents.contains($0.id) } }
    func dismiss(_ e: CalEvent) { dismissedEvents.insert(e.id) }
    @Published var devices: [Device] = []
    private var devicesBusy = false
    private var devicesScanned = false
    private var lowDevices: Set<String> = []
    @Published var timerEnd: Date?
    @Published var timerTotal: TimeInterval = 0
    @Published var shelf: [URL] = (UserDefaults.standard.stringArray(forKey: "shelf") ?? [])
        .map { URL(fileURLWithPath: $0) }.filter { FileManager.default.fileExists(atPath: $0.path) } {
        didSet { UserDefaults.standard.set(shelf.map(\.path), forKey: "shelf") }
    }
    var dragging = false { didSet { if oldValue && !dragging { dragEnded() } } }  // keep the notch open through shelf and fan drags
    var dragEnded: () -> Void = {}
    var presenting = false { didSet { if oldValue && !presenting { presentingEnded() } } }
    var presentingEnded: () -> Void = {}
    let settings = AppSettings.shared
    private let calendar = CalendarSource()
    var toastQueue: [Toast] = []
    private var workingSince: [String: (item: WatchItem, title: String, since: Date)] = [:]
    private var warnedLimits: Set<String> = []
    private var remindedEvents: Set<String> = []
    var debugEvents = false  // a sample list is on screen (debug trigger): don't replace it with the real calendar
    private var lowBatteryWarned = false
    private var thermalsAt = Date.distantPast
    private var readingThermals = false
    private var thermalsWereExpanded = false
    private var fansAt = Date.distantPast
    private var readingFans = false
    private var fanReadRequested = false
    private var lastFanCommandAt: Date?
    private var fanFailures: [Int: Int] = [:]
    @Published private(set) var fanUnavailable = Set<Int>()
    var fanPanelOpen = false { didSet { if fanPanelOpen && !oldValue { updateFans(now: Date(), force: true) } } }
    private var fanOverheatWarned = false
    @Published var expanded = false { didSet {
        if expanded != oldValue { contentSizeChanged() }
        if expanded && !oldValue { Track.event("notch_open") }
    } }
    @Published var load: SystemLoad?
    /// Browsers whose playing tab refuses scripts (the switch is off for its profile): the notch offers to turn it on.
    @Published var browserBlocked: Set<String> = []  // only kept fresh while the notch is open
    @Published var notch = CGSize(width: 200, height: 32)
    @Published var island = false        // drawn as a floating pill instead of hugging the camera housing
    @Published var islandTop: CGFloat = 0  // gap above the pill: under the menu bar edge, or under the housing
    @Published var hasNotch = true
    @Published var sensors = SensorState()
    @Published var call: CallInfo?
    private var sensorBusy = false
    var debugSensors: SensorState?
    /// Volume or brightness the user is changing right now; cleared 1.5 s after the last change.
    @Published var level: LevelHUD?
    private var lastLevels: (device: AudioObjectID?, volume: (value: Double, muted: Bool)?, brightness: Double?)?
    private var levelTimer: Timer?
    private let netMonitor = NWPathMonitor()
    private var netOnline: Bool?
    private var vpnOn: Bool?
    private var downloadNames: Set<String>?  // nil until the first look at ~/Downloads (no toasts for what's already there)
    private var diskWarned = false
    @Published var mirrorOn = false  // the camera preview card in the open notch
    /// The last unlock attempt seen while locked; the lock screen notch shakes red or glows green for a moment.
    @Published var authFlash: (event: AuthEvent, at: Date)?
    var failedUnlocks: [Date] = []  // wrong passwords / fingers since the screen locked, told on unlock  // set by the debug trigger, for checking the call UI by screenshot
    private var callEnded: Date?  // a short gap in mic use (reconnecting, switching device) isn't a new call
    /// Rendered notch size, open and closed, for hover hit-testing. Each keeps its own: SwiftUI reports a size only when
    /// it changes, so a notch reopening at its last size said nothing, and one shared value still held the closed size
    /// (the pointer, well inside, counted as gone and the notch shut).
    var closedSize = CGSize(width: 200, height: 32) { didSet { if closedSize != oldValue { contentSizeChanged() } } }
    var openSize = CGSize(width: 200, height: 32) { didSet { if openSize != oldValue { contentSizeChanged() } } }
    var contentSize: CGSize { expanded ? openSize : closedSize }
    var contentSizeChanged: () -> Void = {}
    var showSettings: () -> Void = {}

    private var lastCPU: [pid_t: Double] = [:]
    private var lastPct: [pid_t: Double] = [:]
    private var lastBusy: [String: Date] = [:]
    private var envCache: [pid_t: [String: String]] = [:]  // a process's start env never changes
    private var argvCache: [pid_t: (name: String, argv: [String])] = [:]
    private var lastTick = DispatchTime.now().uptimeNanoseconds
    private var lastCPUTicks: [UInt32]?
    private let codex = CodexScanner()
    private let antigravity = AntigravityScanner()
    let sync = SyncHub()
    private var usageProc: Process?
    private var claudeUsage: (at: Date, windows: [LimitWindow])?
    private let dev = DevScanner()
    private let github = GitHubWatcher()
    private let files = FileProgressWatcher()
    private var ghWorkers: [pid_t: (job: Job, log: URL?)] = [:]
    private var runnerAlerts: [JobAlert] = []
    private var githubAlerts: [JobAlert] = []
    private var timer: Timer?
    private var ticks = 0

    init() {
        items = (UserDefaults.standard.data(forKey: "items").flatMap { try? JSONDecoder().decode([WatchItem].self, from: $0) }
            ?? Array(presets.prefix(2)))
            // older lists watched Claude by CPU; the session registry is exact
            .map { var i = $0; if i.kind == .process && i.process == "claude" { i.kind = .claude }; return i }
        // AI agents found on this Mac join the list once
        if !UserDefaults.standard.bool(forKey: "addedAgents") {
            for p in presetGroups[0].items where p.isInstalled && !items.contains(where: { $0.kind == p.kind && $0.process == p.process && $0.args == p.args }) {
                items.append(p)
            }
            UserDefaults.standard.set(try? JSONEncoder().encode(items), forKey: "items")  // didSet doesn't run inside init
            UserDefaults.standard.set(true, forKey: "addedAgents")
        }
        // local model runners found on this Mac join the list once too
        if !UserDefaults.standard.bool(forKey: "addedLocalModels") {
            for p in presetGroups.first(where: { $0.title == L("Model running locally") })?.items ?? []
            where p.isInstalled && !items.contains(where: { $0.bundleID == p.bundleID && $0.process == p.process }) {
                items.append(p)
            }
            UserDefaults.standard.set(try? JSONEncoder().encode(items), forKey: "items")
            UserDefaults.standard.set(true, forKey: "addedLocalModels")
        }
        dev.onPush = { [weak self] dir in
            guard let self, self.settings.on(.ci) else { return }
            self.github.pushed(from: dir)
        }
        fanControl.onOverheat = { [weak self] in self?.fanOverheated() }
        fanControl.onCommandAcknowledged = { [weak self] in self?.refreshFansAfterCommand() }
        // AirPods & co. switch the audio output when they connect: rescan right away instead of waiting for the poll
        var outputAddr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                                    mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &outputAddr, .main) { [weak self] _, _ in
            MainActor.assumeIsolated {
                self?.scanDevices()
                // the device list can lag the output switch by a moment
                Task { try? await Task.sleep(for: .seconds(2)); self?.scanDevices() }
            }
        }
        // an event added or edited in Calendar (or synced in) shows up at once instead of at the next 30 s poll
        NotificationCenter.default.addObserver(forName: .EKEventStoreChanged, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshEvents() }
        }
        // macOS calls this the moment the charger is plugged/unplugged or the level changes (no polling delay)
        let context = Unmanaged.passUnretained(self).toOpaque()
        if let source = IOPSNotificationCreateRunLoopSource({ ctx in
            guard let ctx else { return }
            let model = Unmanaged<Model>.fromOpaque(ctx).takeUnretainedValue()
            MainActor.assumeIsolated { model.updateBattery() }
        }, context)?.takeRetainedValue() {
            CFRunLoopAddSource(CFRunLoopGetMain(), source, .defaultMode)
        }
        sync.start(self)
        tick()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick(); self?.sampleSensors(); self?.watchSystem() }
        }
        // shortcut: polled, not observed (CoreAudio listeners per device, brightness has no public event); 7 cheap reads a second
        levelTimer = Timer.scheduledTimer(withTimeInterval: 0.15, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sampleLevels() }
        }
        netMonitor.pathUpdateHandler = { [weak self] path in
            let online = path.status == .satisfied, wifi = path.usesInterfaceType(.wifi)
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.networkChanged(online: online, wifi: wifi) } }
        }
        netMonitor.start(queue: .global(qos: .utility))
        NotificationCenter.default.addObserver(forName: ProcessInfo.thermalStateDidChangeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.thermalChanged() }
        }
    }

    // MARK: Volume & brightness

    func sampleLevels() {
        guard settings.on(.levels) else { lastLevels = nil; return }
        let dev = Levels.output()
        let vol = dev.flatMap(Levels.volume)
        let bri = Levels.brightness()
        defer { lastLevels = (dev, vol, bri) }
        guard let last = lastLevels else { return }  // first reading: nothing changed yet
        var next: LevelHUD?
        // a new output (AirPods connecting) brings its own volume: that isn't the user turning it
        if dev == last.device, let v = vol, let lv = last.volume, abs(v.value - lv.value) > 0.004 || v.muted != lv.muted {
            next = LevelHUD(kind: .volume, value: v.value, muted: v.muted)
        } else if let b = bri, let lb = last.brightness, abs(b - lb) > 0.004 {
            next = LevelHUD(kind: .brightness, value: b)
        }
        guard let next else { return }
        level = next
        let at = next.at
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            MainActor.assumeIsolated { if self?.level?.at == at { self?.level = nil } }
        }
    }

    // MARK: Network, downloads, health

    func networkChanged(online: Bool, wifi: Bool) {
        guard let was = netOnline else { netOnline = online; vpnOn = NetInfo.vpnUp(); return }
        let vpn = NetInfo.vpnUp()
        defer { vpnOn = vpn }
        guard settings.on(.network) else { netOnline = online; return }
        if online != was {
            // a 2 s blip (switching networks) isn't worth a toast: check again before saying anything
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
                MainActor.assumeIsolated {
                    guard let self, self.netMonitor.currentPath.status == .satisfied ? online : !online, self.netOnline != online else { return }
                    self.netOnline = online
                    self.announce(online
                        ? Toast(symbol: wifi ? "wifi" : "network", tint: .green, title: L("Back online"), subtitle: wifi ? L("Wi‑Fi connected") : L("Connected"),
                                event: .netUp, compact: true)
                        : Toast(symbol: "wifi.slash", tint: .red, title: L("Network disconnected"), subtitle: L("Check Wi‑Fi or the network cable"), event: .netDown),
                        notify: !online)
                }
            }
        } else if let v = vpnOn, v != vpn {
            announce(Toast(symbol: vpn ? "lock.shield.fill" : "lock.open.fill", tint: vpn ? .blue : .gray, title: vpn ? L("VPN on") : L("VPN off"),
                           subtitle: vpn ? L("VPN on") : L("VPN off"), event: .netUp, compact: true), notify: false)
        }
    }

    private var systemTicks = 0
    /// Every 2 s: new files in Downloads, VPN switches the path monitor missed; every 10 min: free disk space.
    func watchSystem() {
        systemTicks += 1
        guard systemTicks % 2 == 0 else { return }
        if settings.on(.network), let was = vpnOn, NetInfo.vpnUp() != was { networkChanged(online: netOnline ?? true, wifi: true) }
        if settings.on(.downloads) { scanDownloads() } else { downloadNames = nil }
        if settings.on(.health), systemTicks % 600 == 2 { checkDisk() }
    }

    func scanDownloads() {
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: Downloads.folder.path) else { return }
        let done = Downloads.finished(in: names)
        defer { downloadNames = done }
        guard let before = downloadNames else { return }
        for name in done.subtracting(before).sorted() {
            let url = Downloads.folder.appendingPathComponent(name)
            // moved in from elsewhere long ago (an old file dragged in) isn't a download that just finished
            let made = (try? url.resourceValues(forKeys: [.creationDateKey]))?.creationDate ?? .distantPast
            guard Date().timeIntervalSince(made) < 30 * 60 else { continue }
            announce(Toast(symbol: "arrow.down.circle.fill", tint: .blue, title: L("Download finished"), subtitle: name, event: .downloadDone, url: url, action: L("Show in folder")))
        }
    }

    /// The first time, macOS asks for the camera in a dialog the open notch would cover: close it, ask, then reopen.
    func openMirror() {
        Track.event("mirror_open")
        guard AVCaptureDevice.authorizationStatus(for: .video) == .notDetermined else { mirrorOn = true; return }
        expanded = false
        AVCaptureDevice.requestAccess(for: .video) { ok in
            DispatchQueue.main.async { MainActor.assumeIsolated { if ok { self.mirrorOn = true; self.expanded = true } } }
        }
    }

    func checkDisk(free override: Int64? = nil) {
        guard let free = override ?? NetInfo.freeDisk() else { return }
        let gb = Double(free) / 1e9
        if gb < 10, !diskWarned {
            diskWarned = true
            announce(Toast(symbol: "internaldrive.fill", tint: .orange, title: L("Disk almost full"),
                           subtitle: String(format: L("%.1f GB free"), locale: Locale(identifier: "vi_VN"), gb), event: .warning,
                           url: URL(string: "x-apple.systempreferences:com.apple.settings.Storage"), action: L("Free up space")))
        } else if gb > 15 { diskWarned = false }  // warn again only after it had room
    }

    func thermalChanged(_ forced: ProcessInfo.ThermalState? = nil) {
        guard settings.on(.health) else { return }
        let state = forced ?? ProcessInfo.processInfo.thermalState
        guard state == .serious || state == .critical else { return }
        // just the tool's name ("Gradle", not "Gradle · project"): the subtitle has one line
        let top = states.flatMap(\.jobs).first(where: \.working)?.title.components(separatedBy: " · ").first
        announce(Toast(symbol: "thermometer.high", tint: state == .critical ? .red : .orange,
                       title: state == .critical ? L("Computer is very hot") : L("Computer is warm"),
                       subtitle: top.map { L("{0} is working hard", $0) } ?? L("Consider closing heavy apps"),
                       event: .warning,
                       url: URL(fileURLWithPath: "/System/Applications/Utilities/Activity Monitor.app"), action: L("See apps"), toPhone: true))
    }

    /// HID/SMC reads may block briefly, so the timer only starts them and the result returns to this actor.
    private func updateThermals(now: Date) {
        guard Requirement.fan.met(on: Requirement.current) else { return }
        let interval: TimeInterval = expanded || fanControl.needsControl ? 5 : 30
        let justOpened = expanded && !thermalsWereExpanded
        thermalsWereExpanded = expanded
        guard !readingThermals, justOpened || now.timeIntervalSince(thermalsAt) >= interval else { return }
        thermalsAt = now
        readingThermals = true
        Task.detached {
            let readings = Thermals.all()
            await Task { @MainActor [weak self] in
                self?.thermals = readings
                if readings.contains(where: { $0.celsius >= 95 }) { self?.fanOverheated() }
                else if !readings.isEmpty && readings.allSatisfy({ $0.celsius < 85 }) { self?.fanOverheatWarned = false; self?.fanControl.setHot(false) }
                self?.readingThermals = false
            }.value
        }
    }

    private func fanOverheated() {
        guard fanControl.needsControl, !fanOverheatWarned else { return }
        fanOverheatWarned = true
        fanControl.setHot(true)
        fanControl.stop(fans)
        announce(Toast(symbol: "thermometer.high", tint: .red, title: L("Fans returned to automatic because the Mac is too hot"), event: .warning), notify: false)
    }

    private func refreshFansAfterCommand() {
        let now = Date()
        lastFanCommandAt = now
        updateFans(now: now, force: true)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in
            guard let self else { return }
            self.updateFans(now: Date(), force: true)
        }
    }

    private func updateFans(now: Date, force: Bool = false) {
        guard Requirement.fan.met(on: Requirement.current) else { return }
        if readingFans { if force { fanReadRequested = true }; return }
        guard force || now.timeIntervalSince(fansAt) >= FanNotch.refreshInterval(panelOpen: fanPanelOpen, sinceCommand: lastFanCommandAt.map { now.timeIntervalSince($0) }) else { return }
        fansAt = now; readingFans = true
        Task.detached {
            let reads = Fans.read()
            await Task { @MainActor [weak self] in
                guard let self else { return }
                if self.lastFanCommandAt.map({ now >= $0 }) ?? true {
                    let kept = FanNotch.keepLastGood(previous: self.fans, reads: reads, failures: self.fanFailures)
                    self.fans = kept.fans
                    self.fanFailures = kept.failures
                    self.fanUnavailable = Set(kept.failures.filter { $0.value >= 3 }.map(\.key))
                    if !self.fanOverheatWarned && !reads.isEmpty { self.fanControl.tick(fans: reads, sensors: self.thermals) }
                }
                self.readingFans = false
                if self.fanReadRequested {
                    self.fanReadRequested = false
                    self.updateFans(now: Date(), force: true)
                }
            }.value
        }
    }

    // MARK: Microphone, camera, calls

    func sampleSensors() {
        guard settings.on(.calls) || settings.on(.privacy) else {
            if call != nil || sensors != SensorState() { call = nil; sensors = SensorState() }
            return
        }
        guard !sensorBusy else { return }
        sensorBusy = true
        let debug = debugSensors
        Task.detached {
            let next = debug ?? Sensors.read()
            await MainActor.run { self.sensorBusy = false; self.applySensors(next) }
        }
    }

    func applySensors(_ next: SensorState) {
        var next = next
        if mirrorOn { next.camera = false }  // our own mirror isn't someone else filming
        let prev = sensors
        let now = Date()
        if next != prev { sensors = next }
        let app = settings.on(.calls) ? Sensors.callApp(next.micApps) : nil
        if let app {
            if call == nil {
                // back within 8 s: the same call carrying on (a reconnect), not a new one
                let resumed = callEnded.map { now.timeIntervalSince($0) < 8 } ?? false
                call = CallInfo(app: app, since: resumed ? (lastCallStart ?? now) : now, video: next.camera, muted: next.micMuted)
                if !resumed {
                    lastCallStart = now
                    announce(Toast(symbol: "phone.fill", tint: .green, title: L("On a call"), subtitle: Sensors.name(app),
                                   event: .callStart, compact: true), notify: false)
                }
            } else if call?.app != app || call?.video != next.camera || call?.muted != next.micMuted {
                call?.app = app; call?.video = next.camera; call?.muted = next.micMuted
            }
            callEnded = nil
        } else if let c = call {
            if callEnded == nil { callEnded = now }
            if let e = callEnded, now.timeIntervalSince(e) >= 8 {
                call = nil
                callEnded = nil
                announce(Toast(symbol: "phone.down.fill", tint: .red, title: L("Ended"), subtitle: mmss(e.timeIntervalSince(c.since)),
                               event: .callEnd, compact: true), notify: false)
            }
        }
        // outside a call: say once when something starts recording or filming
        guard settings.on(.privacy), call == nil, app == nil else { return }
        let newMic = next.micApps.filter { !prev.micApps.contains($0) }
        if let id = newMic.first {
            announce(Toast(symbol: "mic.fill", tint: .orange, title: L("Microphone on"), subtitle: Sensors.name(id), event: .sensorOn, compact: true), notify: false)
        } else if next.camera && !prev.camera {
            announce(Toast(symbol: "video.fill", tint: .green, title: L("Camera on"), subtitle: next.micApps.first.map(Sensors.name) ?? "Camera",
                           event: .sensorOn, compact: true), notify: false)
        }
    }
    private var lastCallStart: Date?

    func toggleMute() {
        guard let c = call else { return }
        if Sensors.setInputMuted(!c.muted) { call?.muted = !c.muted; sensors.micMuted = !c.muted }
        else { announce(Toast(symbol: "mic.slash", tint: .orange, title: L("Couldn't mute"), subtitle: L("Use the button in {0}", c.name), event: nil, compact: true), notify: false) }
    }

    func openCallApp() {
        guard let c = call else { return }
        // iPhone and FaceTime calls run in system daemons; their window belongs to FaceTime
        let bid = c.app.hasPrefix("com.apple.") ? "com.apple.FaceTime" : c.app
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bid) {
            NSWorkspace.shared.openApplication(at: url, configuration: .init())
        }
        expanded = false
    }

    func add(_ item: WatchItem) {
        var item = item
        item.id = UUID()
        items.append(item)
    }

    // MARK: Toasts

    func announce(_ t: Toast, notify: Bool = true) {
        sync.record(t)
        if let e = t.event { Sounds.play(e) }
        if notify && settings.systemNotifications { Notifier.post(t) }
        toastQueue.append(t)
        if toast == nil { showNextToast() }
    }

    func dismissToast() { showNextToast() }

    // MARK: Local models

    private func localKey(_ item: WatchItem) -> String? {
        item.bundleID == LocalModels.lmStudio ? "lmstudio" : item.process == "ollama" ? "ollama" : nil
    }

    /// "Đã nạp Gemma 4 26B" for a local model runner; nil for anything else.
    private func modelLine(_ item: WatchItem) -> String? {
        guard let key = localKey(item) else { return nil }
        let names = localModels[key] ?? []
        return names.isEmpty ? L("No model loaded") : L("Loaded ") + names.joined(separator: ", ")
    }

    /// A local runner's row is named after the model it has loaded (its folder means nothing).
    private func modelTitle(_ item: WatchItem) -> String? {
        guard let key = localKey(item), let names = localModels[key], !names.isEmpty else { return nil }
        return names.joined(separator: ", ")
    }

    private func modelBusy(_ item: WatchItem) -> String? { modelTitle(item) == nil ? nil : L("Running") }

    private func refreshLocalModels() {
        let keys = Set(items.filter(\.enabled).compactMap(localKey))
        guard !keys.isEmpty, !readingLocalModels else { return }
        readingLocalModels = true
        DispatchQueue.global(qos: .utility).async {
            var found: [String: [String]] = [:]
            if keys.contains("lmstudio"), NSWorkspace.shared.runningApplications.contains(where: { $0.bundleIdentifier == LocalModels.lmStudio }) {
                found["lmstudio"] = LocalModels.lmStudioLoaded()
            }
            if keys.contains("ollama") { found["ollama"] = LocalModels.ollamaLoaded() }
            DispatchQueue.main.async { MainActor.assumeIsolated { self.localModels = found; self.readingLocalModels = false } }
        }
    }

    // MARK: Agent asks

    /// Picks up asks from the hook, drops ones answered in Claude itself. Off: Claude asks as usual.
    func refreshAsks() {
        let found = AgentAsks.pending() + codexQuestions()
        guard settings.on(.agents) else {
            found.forEach { AgentAsks.send(.pass, to: $0) }
            if !asks.isEmpty { asks = [] }
            return
        }
        let fresh = found.filter { f in !asks.contains { $0.id == f.id } }
        let gone = Set(asks.map(\.id)).subtracting(found.map(\.id))
        if found != asks { asks = found }
        if !gone.isEmpty { dropAskToasts(gone) }
        for a in fresh {
            Track.once("first_ask_shown", ["agent": a.kind.rawValue])
            announce(Toast(symbol: a.isQuestion ? "questionmark.bubble.fill" : "hand.raised.fill", tint: .orange,
                           title: a.kind.name + (a.isQuestion ? L(" is asking") : L(" needs your approval")),
                           subtitle: a.session + " · " + (a.isQuestion ? a.summary : "\(a.action): \(a.summary)"),
                           icon: a.kind.item(in: items), event: .agentAsk, sticky: true, ask: a))
        }
    }

    /// Questions the Codex app left in its threads (no hook involved): your answer goes back as your next message.
    private var answeredCodex: Set<String> = []

    private func codexQuestions() -> [AgentAsk] {
        guard items.contains(where: { $0.enabled && $0.kind == .codex }) else { return [] }
        return codex.questions(now: Date()).compactMap { q, title in
            let id = "codex|\(q.thread)|\(q.callID)"
            guard !answeredCodex.contains(id) else { return nil }
            // the desktop app's own project folders are named g-p-<id>: say "Codex" instead
            return AgentAsk(id: id, pid: 0, session: title.hasPrefix("g-p-") ? "Codex" : title, tool: "request_user_input",
                            summary: q.questions.first?.question ?? "", questions: q.questions, at: q.at, agent: .codex)
        }
    }

    /// `remote`: the answer came from a phone (Sync.swift), which is counted as remote_answer instead.
    func reply(_ a: AgentAsk, _ r: AskReply, remote: Bool = false) {
        Track.event("ask_reply")
        if r != .pass && !remote { Track.once("first_ask_answered", ["agent": a.kind.rawValue]) }
        if a.id.hasPrefix("codex|") {
            answeredCodex.insert(a.id)
            let parts = a.id.split(separator: "|")
            if case .answers(let picked) = r, parts.count == 3 {
                // one question: just the choice; several: one "question: choice" line each
                let text = a.questions.count == 1 ? (picked.values.first ?? "")
                    : a.questions.map { "\($0.question): \(picked[$0.question] ?? "")" }.joined(separator: "\n")
                AgentAsks.codexReply(thread: String(parts[1]), text)
            } else {
                NSWorkspace.shared.runningApplications.first { $0.bundleIdentifier == "com.openai.codex" }?.activate()
            }
            asks.removeAll { $0.id == a.id }
            dropAskToasts([a.id])
            return
        }
        // answering in the agent itself: take the user there (found while the hook is still alive)
        if r == .pass { AgentAsks.focusHost(of: a.pid) }
        AgentAsks.send(r, to: a)
        asks.removeAll { $0.id == a.id }
        dropAskToasts([a.id])
        if asks.isEmpty && r != .pass { expanded = false }
    }

    private func dropAskToasts(_ ids: Set<String>) {
        toastQueue.removeAll { $0.ask.map { ids.contains($0.id) } ?? false }
        if let id = toast?.ask?.id, ids.contains(id) { showNextToast() }
    }

    /// The pointer is on the toast: it stays while you read it or reach for its button, and goes 1.5 s after you leave.
    var toastHovered = false

    private func showNextToast() {
        toast = toastQueue.isEmpty ? nil : toastQueue.removeFirst()
        guard let shown = toast else { return }
        Task {
            try? await Task.sleep(for: .seconds(shown.sticky ? 30 : shown.compact ? 3 : 4.5))
            var calm = Date.distantPast
            while toast?.id == shown.id, toastHovered || Date().timeIntervalSince(calm) < 1.5 {
                if toastHovered { calm = Date() }
                try? await Task.sleep(for: .milliseconds(300))
            }
            if toast?.id == shown.id { showNextToast() }
        }
    }

    // MARK: Timer & shelf

    func startTimer(minutes: Int) {
        Track.event("timer_start", ["minutes": minutes])
        timerTotal = Double(minutes * 60)
        timerEnd = Date().addingTimeInterval(timerTotal)
    }

    func extendTimer(minutes: Int) {
        Track.event("timer_extend", ["minutes": minutes])
        guard let end = timerEnd else { return startTimer(minutes: minutes) }
        timerEnd = end.addingTimeInterval(Double(minutes * 60))
        timerTotal += Double(minutes * 60)
    }

    func stopTimer() { Track.event("timer_stop"); timerEnd = nil }

    func addToShelf(_ urls: [URL]) {
        Track.event("shelf_add", ["count": urls.count])
        for u in urls where u.isFileURL && !shelf.contains(u) { shelf.append(u) }
    }

    /// The + in the shelf: pick files the usual way, several at once.
    func pickShelfFiles() {
        // the open notch's window covers the top middle of the screen, where the file dialog appears: close it first
        expanded = false
        DispatchQueue.main.async {
            NSApp.activate(ignoringOtherApps: true)
            let panel = NSOpenPanel()
            panel.allowsMultipleSelection = true
            panel.canChooseDirectories = true
            panel.prompt = L("Add to shelf")
            guard panel.runModal() == .OK else { return }
            self.addToShelf(panel.urls)
        }
    }

    func receiveDrop(_ providers: [NSItemProvider]) {
        for p in providers {
            _ = p.loadObject(ofClass: URL.self) { url, _ in
                guard let url else { return }
                Task { @MainActor in self.addToShelf([url]) }
            }
        }
    }

    func refreshNowPlaying() {
        guard !npBusy else { return }
        npBusy = true
        let apps = NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular }.compactMap(\.bundleIdentifier)
        let out = AudioOut.bundles()
        // an app is audible when it or one of its helpers (com.google.Chrome.helper…) outputs audio
        let audible = Set(apps.filter { a in out.contains { $0 == a || $0.hasPrefix(a + ".") } })
        let steady = audible.intersection(lastAudible)  // two samples in a row: skip notification blips
        lastAudible = audible
        let last = nowPlaying?.source
        Task.detached {
            var playing: NowPlaying?
            var paused: NowPlaying?
            for bid in NowPlaying.players.keys.sorted() where apps.contains(bid) {
                guard let np = osascript(NowPlaying.playerScript(bid)).flatMap({ NowPlaying.parse($0, source: bid) }) else { continue }
                if np.playing { playing = np; break }
                if paused == nil { paused = np }
            }
            // browsers that let the notch in: ask the tab itself (also gives position and duration)
            var pausedTab: NowPlaying?
            var blocked: Set<String> = []
            for b in BrowserJS.installed where apps.contains(b.bid) && (audible.contains(b.bid) || b.bid == last) {
                // only the browser's own "JavaScript … turned off" counts as the switch being off; any other failure
                // (Automation not allowed, browser busy) must not offer a click that could flip a switch that is on
                let r = osascriptResult(NowPlaying.tabStateScript(b.bid))
                guard let out = r.out else { if r.error.contains("JavaScript") { blocked.insert(b.bid) }; continue }
                guard playing == nil, let np = NowPlaying.parseTab(out, source: b.bid) else { continue }
                if np.playing { playing = np } else if pausedTab == nil && last == b.bid { pausedTab = np }  // stays, paused, after you pause it
            }
            if playing == nil, pausedTab == nil, let bid = apps.first(where: { steady.contains($0) && NowPlaying.players[$0] == nil }) {
                let title = NowPlaying.browsers.contains(bid) ? osascript(NowPlaying.tabScript(bid)).flatMap { $0.isEmpty ? nil : $0 } : nil
                playing = NowPlaying(source: bid, title: title, playing: true)
            }
            let result = playing ?? pausedTab ?? paused
            await MainActor.run {
                self.npBusy = false
                if blocked != self.browserBlocked { self.browserBlocked = blocked }
                if result != self.nowPlaying {
                    if result?.title != self.nowPlaying?.title || result?.source != self.nowPlaying?.source {
                        NSLog("NotchTasks now playing: %@ — %@ (%@)", result?.source ?? "-", result?.title ?? "-", result?.playing == true ? "playing" : "paused")
                    }
                    self.nowPlaying = result
                }
            }
        }
    }

    func control(_ cmd: MediaCommand) {
        guard let np = nowPlaying else { return }
        Track.event("media_control", ["cmd": "\(cmd)".components(separatedBy: "(").first ?? "", "kind": NowPlaying.players[np.source] != nil ? "player" : NowPlaying.browsers.contains(np.source) ? "browser" : "key"])
        if NowPlaying.players[np.source] != nil {
            if case .seek = cmd { return }
            let verb = cmd == .toggle ? "playpause" : cmd == .next ? "next track" : "previous track"
            Task.detached { _ = osascript("tell application id \"\(np.source)\" to \(verb)") }
        } else if NowPlaying.browsers.contains(np.source) {
            // the media keys go to whichever app macOS last saw playing, often a paused Music: talk to the tab instead
            let script = NowPlaying.tabControlScript(np.source, cmd)
            let otherPlayer = NowPlaying.players.keys.contains { !NSRunningApplication.runningApplications(withBundleIdentifier: $0).isEmpty }
            Task.detached {
                let out = osascript(script)
                await MainActor.run {
                    // the page says what it did: the icon follows that, not a guess
                    if out == "play" { self.nowPlaying?.playing = true } else if out == "pause" { self.nowPlaying?.playing = false }
                    if out != nil { return }
                    if !otherPlayer { self.sendMediaKey(cmd); return }  // nothing else could take the key
                    self.enableBrowserControl(np.source)
                }
            }
        } else {
            sendMediaKey(cmd)
        }
        // optimistic for players; browser tabs set it from their answer above
        if cmd == .toggle && !NowPlaying.browsers.contains(np.source) { nowPlaying?.playing.toggle() }
    }

    /// The notch's "Bật" button: a guide window the user follows; it notices by itself when the switch is on.
    func enableBrowserControl(_ bid: String) {
        guard let b = BrowserJS.browser(bid) else { return }
        Track.event("browser_guide", ["browser": b.name])
        expanded = false
        DispatchQueue.main.async {
            BrowserGuide.show(b) { [weak self] in
                Track.event("browser_control_on", ["browser": b.name])
                self?.browserBlocked.remove(bid)
                self?.announce(Toast(symbol: "checkmark.circle.fill", tint: .green, title: L("{0} controls on", b.name),
                                     subtitle: L("The notch can now control music in the tab"), event: nil), notify: false)
            }
        }
    }

    private func sendMediaKey(_ cmd: MediaCommand) {
        let prompt = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        guard AXIsProcessTrustedWithOptions(prompt) else { return }
        postMediaKey(cmd)
    }

    /// Jump within the track; only the players with an AppleScript dictionary (Spotify, Music) can.
    func seek(to seconds: Double) {
        guard let np = nowPlaying else { return }
        let s = max(0, seconds)
        if NowPlaying.browsers.contains(np.source) {
            control(.seek(s))
            nowPlaying?.position = s
            return
        }
        guard NowPlaying.players[np.source] != nil else { return }
        Task.detached { _ = osascript("tell application id \"\(np.source)\" to set player position to \(String(format: "%.1f", s))") }
        nowPlaying?.position = s  // optimistic; the next refresh corrects it
    }

    func dismiss(_ alert: JobAlert) {
        alerts.removeAll { $0.id == alert.id }
        runnerAlerts.removeAll { $0.id == alert.id }
        githubAlerts.removeAll { $0.id == alert.id }
        let dismissed = (UserDefaults.standard.stringArray(forKey: "dismissedJobs") ?? []) + [alert.id]
        UserDefaults.standard.set(Array(dismissed.suffix(1000)), forKey: "dismissedJobs")
    }

    private(set) var runnerJobs: [RunnerJob] = []
    private var runnerLogCache: [URL: (at: Date, parsed: (running: [RunnerJob], failed: [JobAlert], succeeded: Set<String>))] = [:]

    func scanRunnerLog() {
        var failed: [JobAlert] = []
        var running: [RunnerJob] = []
        var succeeded: Set<String> = []
        for url in RunnerLog.files() {
            let m = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            // parsing half a megabyte every 3 s cost ~10% CPU: only again when the log was written to
            let parsed = runnerLogCache[url].flatMap { $0.at == m ? $0.parsed : nil } ?? RunnerLog.parse(readTail(url, 512_000))
            runnerLogCache[url] = (m, parsed)
            failed += parsed.failed
            succeeded.formUnion(parsed.succeeded)
            // a running job appends its trace every minute; a silent log means the runner died mid-job
            if Date().timeIntervalSince(m) < 5 * 60 { running += parsed.running }
        }
        // jobs that just left the running list: announce how they ended
        for gone in runnerJobs where !running.contains(where: { $0.id == gone.id }) {
            if failed.contains(where: { $0.id == gone.id }) {
                announce(Toast(symbol: "xmark.octagon.fill", tint: .red, title: L("CI failed: {0}", gone.name), subtitle: gone.project, event: .ciFailed, url: gone.url, action: L("View log")))
            } else if succeeded.contains(gone.id) {
                announce(Toast(symbol: "checkmark.seal.fill", tint: .green, title: L("CI passed: {0}", gone.name), subtitle: gone.project, event: .ciPassed, url: gone.url))
            }
        }
        runnerJobs = running
        // first run: failures already in the log are history, not news
        guard let dismissed = UserDefaults.standard.stringArray(forKey: "dismissedJobs") else {
            UserDefaults.standard.set(failed.map(\.id), forKey: "dismissedJobs")
            return
        }
        let seen = Set(dismissed)
        var unique: [JobAlert] = []
        for a in failed.reversed() where !seen.contains(a.id) && !unique.contains(where: { $0.id == a.id }) { unique.append(a) }
        runnerAlerts = unique  // newest first
        publishAlerts()
    }

    private func publishAlerts() {
        let all = githubAlerts + runnerAlerts
        if all != alerts { alerts = all }
    }

    /// GitHub runs that just ended: a toast either way, failures stay as alerts until dismissed.
    private func githubFinished(_ runs: [GHRun]) {
        let dismissed = Set(UserDefaults.standard.stringArray(forKey: "dismissedJobs") ?? [])
        for r in runs {
            if r.conclusion == "success" {
                announce(Toast(symbol: "checkmark.seal.fill", tint: .green, title: L("CI passed: {0}", r.workflow), subtitle: r.repo, event: .ciPassed, url: r.url))
            } else if ["failure", "timed_out", "startup_failure"].contains(r.conclusion ?? "") {
                announce(Toast(symbol: "xmark.octagon.fill", tint: .red, title: L("CI failed: {0}", r.workflow), subtitle: r.repo, event: .ciFailed, url: r.url, action: L("View log")))
                let a = JobAlert(id: "gh-\(r.id)", name: r.workflow, project: r.repo, reason: r.title.isEmpty ? "failure" : r.title, url: r.url)
                if !dismissed.contains(a.id) { githubAlerts.insert(a, at: 0) }
            }
        }
        publishAlerts()
    }

    func open(_ item: WatchItem) {
        Track.event("item_open", ["kind": "\(item.kind)"])
        guard let id = item.bundleID, let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) else { return }
        NSWorkspace.shared.openApplication(at: url, configuration: .init())
    }

    func tick() {
        // the phone's pets on the notch: worried while an agent waits, sad over a failed job, focused with a timer, asleep at night
        let hour = Calendar.current.component(.hour, from: Date())
        Animations.petPose = !asks.isEmpty ? 5 : !alerts.isEmpty ? 4 : timerEnd != nil ? 6 : (hour >= 23 || hour < 6) ? 2 : 0
        let now = Date()
        updateThermals(now: now)
        updateFans(now: now)
        let t = DispatchTime.now().uptimeNanoseconds
        let elapsed = Double(max(t - lastTick, 1))
        // an extra tick right after the timer's (settings edits…) would divide a few ms of CPU by a few ms: keep the last reading
        let sampled = elapsed >= 0.5e9
        if sampled { lastTick = t }
        let procs = Proc.all()
        let kids = Dictionary(grouping: procs, by: \.ppid)
        let byName = Dictionary(grouping: procs, by: \.name)
        let names = Dictionary(procs.map { ($0.pid, $0.name) }, uniquingKeysWith: { a, _ in a })
        var nextCPU: [pid_t: Double] = [:]
        var nextPct: [pid_t: Double] = [:]
        var busy: [String: Date] = [:]

        func cpu(_ pid: pid_t) -> Double {
            guard let t = Proc.cpuTime(pid) else { return 0 }
            guard sampled else {
                nextCPU[pid] = lastCPU[pid] ?? t
                return lastPct[pid] ?? 0
            }
            nextCPU[pid] = t
            let pct = lastCPU[pid].map { max(0, t - $0) / elapsed * 100 } ?? 0
            nextPct[pid] = pct
            return pct
        }
        // CPU of a whole process tree, plus its busiest descendant = "what it is running"
        func tree(_ root: pid_t) -> (total: Double, top: String?, topCPU: Double) {
            var total = cpu(root), top: String?, topCPU = 1.0
            var stack = [root]
            while let p = stack.popLast() {
                for c in kids[p] ?? [] {
                    let v = cpu(c.pid)
                    total += v
                    if v > topCPU { top = c.name; topCPU = v }
                    stack.append(c.pid)
                }
            }
            return (total, top, topCPU)
        }
        // stays "working" 6s after the last busy sample so short pauses don't flicker
        func job(_ pid: pid_t, title: String, threshold: Double, idle: String, busy busyText: String? = nil) -> Job {
            let key = "\(pid)"
            let (total, top, _) = tree(pid)
            if total > threshold { busy[key] = now } else if let b = lastBusy[key], now.timeIntervalSince(b) < 6 { busy[key] = b }
            let working = busy[key] != nil
            let pct = "\(Int(total.rounded()))% CPU"
            // a local model runner says which model is generating, not which helper process
            let detail = working ? busyText.map { "\($0) · \(pct)" } ?? top.map { "▶ \($0) · \(pct)" } ?? L("Working · {0}", pct) : idle
            return Job(id: key, title: title, detail: detail, working: working)
        }

        // a process's command line never changes; read it once per pid
        func argv(_ pid: pid_t) -> [String] {
            if let c = argvCache[pid], c.name == names[pid] { return c.argv }
            let a = Proc.argv(pid)
            argvCache[pid] = (names[pid] ?? "", a)
            return a
        }

        // first process in the tree that carries CI_PROJECT_PATH
        func ciEnv(under root: pid_t) -> [String: String] {
            var queue = [root]
            while !queue.isEmpty {
                let p = queue.removeFirst()
                let env = envCache[p] ?? Proc.ciEnv(p)
                envCache[p] = env
                if env["CI_PROJECT_PATH"] != nil { return env }
                queue += (kids[p] ?? []).map(\.pid)
            }
            return [:]
        }

        // the job's shell may sit in $HOME while a child shell runs inside the checkout
        func buildsProject(under root: pid_t) -> String? {
            var queue = [root]
            while !queue.isEmpty {
                let p = queue.removeFirst()
                if let project = Proc.cwd(p).flatMap(Proc.buildsProject) { return project }
                queue += (kids[p] ?? []).map(\.pid)
            }
            return nil
        }

        let build = settings.on(.ci)
        if build, ticks % 3 == 0, byName["gitlab-runner"] != nil || !runnerJobs.isEmpty {
            scanRunnerLog()
        } else if !build {
            runnerJobs = []
            runnerAlerts = []
            githubAlerts = []
            publishAlerts()
        }
        if settings.on(.media) {
            if ticks % 2 == 0 { refreshNowPlaying() }
        } else if nowPlaying != nil {
            nowPlaying = nil
        }
        modulesTick(now)

        // Build & CI: local tasks, downloads, GitLab runner jobs, GitHub runs — all detected, nothing to configure
        var buildJobs: [Job] = []
        if build {
            files.start()
            buildJobs = dev.scan(procs, kids: kids, cpu: cpu, now: now) + files.jobs()  // the scan also spots git pushes
            for p in byName["gitlab-runner"] ?? [] where names[p.ppid] != "gitlab-runner" {
                let children = (kids[p.pid] ?? []).sorted { $0.pid < $1.pid }
                if !runnerJobs.isEmpty {
                    // the runner's log names each running job; pair with job processes (both in start order) for CPU/command
                    for (i, rj) in runnerJobs.enumerated() {
                        let j = i < children.count ? job(children[i].pid, title: rj.project, threshold: 0.5, idle: "") : nil
                        buildJobs.append(Job(id: "gl:\(rj.id)", title: rj.project, detail: rj.name + (j.map { " · " + $0.detail } ?? ""),
                                             working: true, url: rj.url, symbol: "server.rack", tool: "GitLab Runner"))
                    }
                } else {
                    for c in children {
                        let j = job(c.pid, title: c.name, threshold: 0.5, idle: L("Waiting"))
                        // project + job name from the job's CI_* env, else from the builds path
                        let ci = ciEnv(under: c.pid)
                        let name = ci["CI_JOB_NAME"].map { [ci["CI_JOB_STAGE"], $0].compactMap { $0 }.joined(separator: " › ") + " · " } ?? ""
                        buildJobs.append(Job(id: "gl:\(c.pid)", title: ci["CI_PROJECT_PATH"] ?? buildsProject(under: c.pid) ?? "GitLab",
                                             detail: name + j.detail, working: j.working, symbol: "server.rack", tool: "GitLab Runner"))
                    }
                }
            }
            if ticks % 10 == 0 { github.poll { [weak self] in self?.githubFinished($0) } }
            let workers = Set((byName["Runner.Worker"] ?? []).map(\.pid))
            for (pid, w) in ghWorkers where !workers.contains(pid) {
                let result = GHSelfHosted.result(w.log.map { readTail($0, 2_000_000) })
                let ok = result == "Succeeded"
                if let result {
                    announce(Toast(symbol: ok ? "checkmark.seal.fill" : "xmark.octagon.fill", tint: ok ? .green : .red,
                                   title: (ok ? L("CI passed: ") : L("CI failed: ")) + w.job.detail, subtitle: w.job.title, event: ok ? .ciPassed : .ciFailed))
                }
            }
            ghWorkers = ghWorkers.filter { workers.contains($0.key) }
            for pid in workers where ghWorkers[pid] == nil || (ticks % 10 == 0 && ghWorkers[pid]?.job.detail == "job") {
                let log = ghWorkers[pid]?.log ?? GHSelfHosted.logURL(pid)
                ghWorkers[pid] = (GHSelfHosted.job(worker: pid, kids: kids, log: log), log)
            }
            buildJobs += github.jobs + ghWorkers.values.map(\.job).sorted { $0.id < $1.id }
        } else {
            files.stop()
        }

        // remembered so Settings can list the tools this Mac actually runs, each with an editable icon
        for t in Set(buildJobs.compactMap(\.tool)) where !settings.seenTools.contains(t) { settings.seenTools.insert(t, at: 0) }
        var result: [ItemState] = buildJobs.isEmpty ? [] : [ItemState(item: buildItem, jobs: buildJobs)]
        // music players are shown by Now Playing, not as tasks; the agents module can be turned off entirely
        for item in items where settings.on(item.inBuild ? .ci : .agents) && item.enabled && NowPlaying.players[item.bundleID ?? ""] == nil {
            var jobs: [Job] = []
            var alive = false
            switch item.kind {
            case .process where item.cli == "agy":
                jobs = antigravity.jobs(now: now)
            case .process:
                for p in item.roots(item.process.isEmpty ? procs : byName[item.process] ?? [], argv: argv) {
                    alive = true
                    if item.childTasks {
                        for c in kids[p.pid] ?? [] {
                            jobs.append(job(c.pid, title: Proc.folder(c.pid) ?? c.name, threshold: item.cpuThreshold, idle: L("Waiting")))
                        }
                    } else {
                        jobs.append(job(p.pid, title: modelTitle(item) ?? Proc.folder(p.pid) ?? item.name, threshold: item.cpuThreshold, idle: modelLine(item) ?? L("Waiting"), busy: modelBusy(item)))
                    }
                }
            case .app:
                for app in NSWorkspace.shared.runningApplications where app.bundleIdentifier == item.bundleID {
                    jobs.append(job(app.processIdentifier, title: modelTitle(item) ?? app.localizedName ?? item.name, threshold: item.cpuThreshold, idle: modelLine(item) ?? L("Open"), busy: modelBusy(item)))
                }
            case .codex:
                jobs = codex.jobs(now: now)
            case .dev:
                break  // the built-in Build & CI row, never a list item
            case .claude:
                // skip files left by dead sessions, and our own usage check
                for s in ClaudeSession.all() where names[s.pid] == "claude" && s.pid != usageProc?.processIdentifier {
                    let busy = s.status == "busy"
                    let since = ago(now.timeIntervalSince1970 - (s.statusUpdatedAt ?? 0) / 1000)
                    let top = busy ? tree(s.pid).top : nil
                    let detail = top.map { "▶ \($0) · \(since)" } ?? (busy ? L("Working · {0}", since) : L("Waiting"))
                    let title = s.name ?? s.cwd.map { ($0 as NSString).lastPathComponent } ?? "Claude"
                    jobs.append(Job(id: "\(s.pid)", title: title, detail: detail, working: busy))
                }
            }
            if alive || !jobs.isEmpty {
                result.append(ItemState(item: item, jobs: jobs.sorted { $0.working && !$1.working }))
            }
        }
        lastCPU = nextCPU
        if sampled { lastPct = nextPct }
        lastBusy = busy
        envCache = envCache.filter { names[$0.key] != nil }
        argvCache = argvCache.filter { names[$0.key] != nil }
        if result != states { states = result }
        announceFinished(result, now)

        if ticks % 10 == 0 {
            if settings.on(.limits), items.contains(where: { $0.enabled && $0.kind == .claude }), ticks % 180 == 0 { refreshClaudeUsage() }
            // Antigravity's quota comes from its own running server: asked once a minute, gone when it is not running
            if ticks % 60 == 0 {
                if settings.on(.limits), items.contains(where: { $0.enabled && $0.cli == "agy" }) {
                    Task { [weak self] in
                        let found = await AntigravityLimits.fetch()
                        self?.antigravityLimits = found
                    }
                } else {
                    antigravityLimits = nil
                }
            }
            refreshLimits(now)
        }
        if settings.systemLoad, let cpuNow = SystemLoad.cpuTicks() {
            // sampled every second so the first reading is ready the moment the notch opens
            if expanded, let before = lastCPUTicks, let mem = SystemLoad.memory() { load = SystemLoad(cpu: SystemLoad.cpu(from: before, to: cpuNow), memory: mem) }
            lastCPUTicks = cpuNow
        }
        refreshAsks()  // backup for a missed notice, and clears asks answered in Claude
        sync.publish()
        if ticks % 30 == 0 { refreshLocalModels() }
        ticks += 1
    }

    private var antigravityLimits: ProviderLimits?

    private func refreshLimits(_ now: Date) {
        let enabled = Set(items.filter(\.enabled).map(\.kind))
        let l = settings.on(.limits) ? [enabled.contains(.claude) ? claudeNow(now) : nil,
                                        enabled.contains(.codex) ? codex.limits(now: now) : nil,
                                        antigravityLimits].compactMap { $0 } : []
        if l != limits { limits = l }
        // every window counts (a weekly limit near its end matters as much as the 5 h one). The steps (80, 85, 90…, 99,
        // 100) only decide when to warn, once each per window; the toast says the real number
        for p in l {
            for w in p.windows where Double(LimitWindow.step(w.used)) >= settings.limitWarn {
                let step = LimitWindow.step(w.used)
                // by the hour: Antigravity reports the same reset a few seconds apart from one asking to the next
                let key = "\(p.name)-\(w.label)-\(Int(w.resets.timeIntervalSince1970) / 3600)-\(step)"
                guard warnedLimits.insert(key).inserted else { continue }
                let at = w.resetText()
                let left = Int(max(0, w.resets.timeIntervalSinceNow) / 60)
                let wait = left >= 60 ? L("{0}h {1}m", left / 60, left % 60) : L("{0} min", left)
                announce(Toast(symbol: step == 100 ? "hourglass" : "gauge.with.dots.needle.100percent", tint: step >= 95 ? .red : .orange,
                               title: step == 100 ? L("{0} reached its {1} limit", p.name, w.label) : L("{0} used {1}% of its {2} limit", p.name, Int(w.used), w.label),
                               subtitle: step == 100 ? L("Wait until {0} ({1} left)", at, wait) : L("Resets at {0}", at),
                               event: .limitWarn, sticky: true))
            }
        }
    }

    /// Live plan usage (fresh for 15 min), else what the terminal statusline last saw.
    private func claudeNow(_ now: Date) -> ProviderLimits? {
        guard let u = claudeUsage, now.timeIntervalSince(u.at) < 15 * 60 else { return claudeLimits(now: now) }
        let windows = u.windows.map { LimitWindow(label: $0.label, used: $0.resets > now ? $0.used : 0, resets: $0.resets) }
        return ProviderLimits(kind: .claude, name: "Claude", bundleID: "com.anthropic.claudefordesktop", windows: windows, updated: u.at)
    }

    func refreshClaudeUsage() {
        guard usageProc == nil, let cli = ClaudeUsage.cli else { return }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: cli)
        p.arguments = ["-p", "/usage", "--no-session-persistence"]
        p.currentDirectoryURL = FileManager.default.temporaryDirectory
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        p.terminationHandler = { [weak self] _ in
            let text = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.usageProc = nil
                    let windows = ClaudeUsage.parse(text, now: Date())
                    if windows.isEmpty { NSLog("NotchTasks: claude /usage gave no numbers") } else {
                        self.claudeUsage = (Date(), windows)
                        self.refreshLimits(Date())  // show it now, not at the next 10 s round
                    }
                }
            }
        }
        guard (try? p.run()) != nil else { return }
        usageProc = p
        DispatchQueue.main.asyncAfter(deadline: .now() + 30) { if p.isRunning { p.terminate() } }  // never hang on a stuck CLI
    }

    /// A task that was working and stopped (after running at least `doneAfter` s) is announced as done.
    /// GitLab jobs are skipped here: the runner log says whether they passed or failed.
    private func announceFinished(_ result: [ItemState], _ now: Date) {
        guard settings.on(.agents) || settings.on(.ci) else { workingSince = [:]; return }
        var current: [String: (item: WatchItem, title: String, since: Date)] = [:]
        for st in result {
            // CI jobs report pass/fail themselves; local tasks only end
            for j in st.jobs where j.working && !(st.item.kind == .dev && (j.id.hasPrefix("gl:") || j.id.hasPrefix("gh"))) {
                let key = "\(st.item.id)/\(j.id)"
                current[key] = (st.item, j.title, workingSince[key]?.since ?? now)
            }
        }
        for (key, w) in workingSince where current[key] == nil {
            let took = now.timeIntervalSince(w.since)
            guard took >= settings.doneAfter else { continue }
            // dev tasks name the tool ("Gradle · app done"); the exit code of a process we didn't start isn't readable
            let title = L("{0} done", w.item.kind == .dev ? w.title : w.item.name)
            announce(Toast(symbol: "checkmark.circle.fill", tint: .green, title: title, subtitle: w.item.kind == .dev ? ago(took) : "\(w.title) · \(ago(took))",
                           icon: w.item.kind == .dev ? nil : w.item, event: .taskDone, toPhone: true))
        }
        workingSince = current
    }

    /// Bluetooth + USB scan off the main thread; connects/disconnects since the last scan are announced.
    func scanDevices() {
        guard settings.on(.devices) else {
            if !devices.isEmpty { devices = [] }
            devicesScanned = false
            return
        }
        guard !devicesBusy else { return }
        devicesBusy = true
        Task.detached {
            let found = Devices.bluetooth() + Devices.usb()
            await MainActor.run { self.devicesFound(found) }
        }
    }

    private func devicesFound(_ found: [Device]) {
        devicesBusy = false
        if devicesScanned {  // the first scan is the baseline, not news
            let tint = settings.color(for: .devices) ?? .blue
            for d in found where !devices.contains(where: { $0.id == d.id }) {
                announce(Toast(symbol: d.symbol, tint: tint, title: L("{0} connected", d.name), subtitle: d.battery ?? L("Ready"), event: .deviceOn,
                               art: .device(art: d.art, symbol: d.symbol, connected: true)), notify: false)
            }
            for d in devices where !found.contains(where: { $0.id == d.id }) {
                announce(Toast(symbol: d.symbol, tint: .gray, title: L("{0} disconnected", d.name), event: .deviceOff,
                               art: .device(art: d.art, symbol: d.symbol, connected: false)), notify: false)
            }
        }
        for d in found {
            guard let low = d.lowest else { continue }
            if low <= 15, lowDevices.insert(d.id).inserted {
                announce(Toast(symbol: "battery.25percent", tint: .red, title: L("{0} battery low", d.name), subtitle: d.battery ?? "", event: .lowBattery))
            } else if low > 30 {
                lowDevices.remove(d.id)
            }
        }
        devicesScanned = true
        if found != devices { devices = found }
    }

    func updateBattery() {
        guard settings.on(.battery) else { battery = nil; return }
        guard let b = BatteryState.read() else { return }
        if let old = battery, old.plugged != b.plugged {
            announce(Toast(symbol: b.plugged ? "bolt.fill" : "powerplug.portrait", tint: b.plugged ? .green : .gray,
                           title: b.plugged ? L("Charging") : L("Unplugged"), subtitle: "\(b.percent)%", event: b.plugged ? .charging : .unplugged, compact: true,
                           art: .charge(percent: b.percent, plugged: b.plugged)), notify: false)
        }
        if b.plugged { lowBatteryWarned = false }
        if !b.plugged && b.percent <= 20 && !lowBatteryWarned {
            lowBatteryWarned = true
            announce(Toast(symbol: "battery.25percent", tint: .red, title: L("Low battery"), subtitle: L("{0}% left", b.percent), event: .lowBattery))
        }
        if b != battery { battery = b }
    }

    func refreshEvents() {
        guard settings.on(.calendar), !debugEvents else { return }
        let list = calendar.events(within: 12)
        if list != events { events = list }
        let ids = Set(list.map(\.id))
        if !dismissedEvents.isSubset(of: ids) { dismissedEvents.formIntersection(ids) }
    }

    /// Battery, calendar and timer: cheap polls, each only while its module is on.
    private func modulesTick(_ now: Date) {
        // power-source events update it instantly; this slow poll is only a fallback
        if ticks % 30 == 0 || (!settings.on(.battery) && battery != nil) { updateBattery() }
        if ticks % 5 == 0 { scanDevices() }

        if settings.on(.calendar) {
            if ticks % 30 == 0 { refreshEvents() }
            if let e = visibleEvents.first(where: { !$0.allDay && (0...300).contains($0.start.timeIntervalSince(now)) }),
               remindedEvents.insert(e.id).inserted {
                announce(Toast(symbol: "calendar", tint: e.color, title: e.title,
                               subtitle: L("At ") + e.start.formatted(date: .omitted, time: .shortened), event: .meeting, url: e.link))
            }
        } else if !events.isEmpty {
            events = []
        }

        if let end = timerEnd, now >= end {
            timerEnd = nil
            announce(Toast(symbol: "timer", tint: .orange, title: L("Time's up"), subtitle: L("{0} min", Int(timerTotal / 60)), event: .timerDone))
        }
    }
}
