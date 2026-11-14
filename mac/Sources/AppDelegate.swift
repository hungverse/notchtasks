import AppKit
import SwiftUI
import Combine
import ServiceManagement

// MARK: - Window

final class NotchPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// NSMenuItem that runs a closure (keeps the status menu declarative).
final class ClosureMenuItem: NSMenuItem {
    private let run: () -> Void
    init(_ title: String, _ symbol: String? = nil, _ run: @escaping () -> Void) {
        self.run = run
        super.init(title: title, action: #selector(fire), keyEquivalent: "")
        target = self
        if let symbol { image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil) }
    }
    required init(coder: NSCoder) { fatalError("not used") }
    @objc private func fire() { run() }
}

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate {
    let model = Model()
    let permissions = Permissions()
    let settings = AppSettings.shared
    let updater = Updater()
    var panel: NSPanel!
    var lockPanel: NSPanel?
    var isLocked = false
    var settingsWindow: NSWindow?
    var onboardingWindow: NSWindow?
    var statusItem: NSStatusItem?
    var screen: NSScreen?
    private var hoverWork: DispatchWorkItem?
    private var collapseWork: DispatchWorkItem?
    private let authWatch = AuthWatch()
    private var bag: Set<AnyCancellable> = []

    func applicationDidFinishLaunching(_ n: Notification) {
        Updater.removeLeftoversAtLaunch()
        NotificationCenter.default.addObserver(self, selector: #selector(languageChanged), name: L10n.changed, object: nil)
        panel = NotchPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.mainMenu.rawValue + 3)
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.ignoresMouseEvents = true // clicks pass through until hovered
        let host = NSHostingView(rootView: LocalizedRoot { NotchView(model: self.model) })
        // the panel's frame is set by layout(): without this SwiftUI re-measures the whole tree on every animation frame
        host.sizingOptions = []
        panel.contentView = host
        model.contentSizeChanged = { [weak self] in self?.layout() }
        model.dragEnded = { [weak self] in self?.trackMouse(.mouseMoved) }
        model.presentingEnded = { [weak self] in self?.trackMouse(.mouseMoved) }
        model.showSettings = { [weak self] in self?.showSettings() }
        updater.pendingChanged = { [weak self] pending in self?.model.updatePending = pending }
        updater.start { [weak self] update in
            self?.model.announce(Toast(symbol: "arrow.down.circle.fill", tint: .blue, title: L("New version {0}", update.version),
                                       event: nil, action: L("Control Panel"), pane: "overview"), notify: false)
        }
        layout()
        panel.orderFrontRegardless()

        // start at login once, only from /Applications; never re-enable if the user turned it off
        if Bundle.main.bundlePath.hasPrefix("/Applications/"), !UserDefaults.standard.bool(forKey: "loginItemSet") {
            do {
                try SMAppService.mainApp.register()
                UserDefaults.standard.set(true, forKey: "loginItemSet")
            } catch { NSLog("NotchTasks: login item failed: \(error)") }
        }

        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.layout() }
        }
        // mouse monitoring needs no Accessibility permission
        let events: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseDragged, .leftMouseUp]
        NSEvent.addGlobalMonitorForEvents(matching: events) { [weak self] e in
            let type = e.type
            MainActor.assumeIsolated { self?.trackMouse(type) }
        }
        NSEvent.addLocalMonitorForEvents(matching: events) { [weak self] e in
            self?.trackMouse(e.type)
            return e
        }
        // the view may open the notch itself (click mode): keep the panel clickable then
        model.$expanded.removeDuplicates().dropFirst().sink { [weak self] open in
            guard let self else { return }
            Sounds.play(open ? .open : .close)
            // closed from code (a button, a dialog) rather than by the pointer leaving: stop catching clicks right away
            guard open else { self.panel.ignoresMouseEvents = true; return }
            self.panel.ignoresMouseEvents = false
            // .levelChange is too faint to notice on most trackpads; .alignment is the clear "snap" other notch apps use
            if self.settings.haptics { NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now) }
        }
        .store(in: &bag)
        settings.$menuBarIcon.sink { [weak self] show in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.updateStatusItem(show) } }
        }
        .store(in: &bag)

        // lock screen: a display-only copy of the notch, shown above it while locked
        let dnc = DistributedNotificationCenter.default()
        dnc.addObserver(forName: .init("com.apple.screenIsLocked"), object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.screenLocked(true) }
        }
        dnc.addObserver(forName: .init("com.apple.screenIsUnlocked"), object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.screenLocked(false) }
        }
        // the agent hook wrote an ask: show it now, not on the next tick
        dnc.addObserver(forName: AgentAsks.notice, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.model.refreshAsks() }
        }
        // for checking the notch by screenshot without touching the pointer: "open", "close", or "event" (a fake meeting
        // in 4 minutes, kept in memory only; the next calendar read, within 30 s, replaces it)
        dnc.addObserver(forName: .init("local.notchtasks.debug.expand"), object: nil, queue: .main) { [weak self] n in
            MainActor.assumeIsolated {
                guard let self else { return }
                switch n.object as? String {
                case "event", "event-now":
                    // sample day, in memory only; "noevent" goes back to the real calendar
                    let now = Date(), soon = (n.object as? String) == "event"
                    self.model.debugEvents = true
                    self.model.events = [
                        CalEvent(id: "d0", title: L("Lan's birthday"), start: Calendar.current.startOfDay(for: now), end: Calendar.current.startOfDay(for: now).addingTimeInterval(86400),
                                 color: .pink, link: nil, allDay: true, calendar: L("Family")),
                        CalEvent(id: "d5", title: L("Time off"), start: Calendar.current.startOfDay(for: now), end: Calendar.current.startOfDay(for: now).addingTimeInterval(3 * 86400),
                                 color: .teal, link: nil, allDay: true, calendar: L("Work")),
                        CalEvent(id: "d1", title: L("Write report"), start: now.addingTimeInterval(-20 * 60), end: now.addingTimeInterval(25 * 60), color: .orange, link: nil),
                        CalEvent(id: "d2\(soon)", title: L("Sprint meeting"), start: now.addingTimeInterval(soon ? 4 * 60 + 5 : 50 * 60), end: now.addingTimeInterval(soon ? 34 * 60 : 80 * 60),
                                 color: .blue, link: URL(string: "https://meet.google.com/")),
                        CalEvent(id: "d3", title: L("Team lunch"), start: now.addingTimeInterval(95 * 60), end: now.addingTimeInterval(155 * 60), color: .green, link: nil),
                        CalEvent(id: "d4", title: L("Customer call"), start: now.addingTimeInterval(180 * 60), end: now.addingTimeInterval(200 * 60), color: .purple,
                                 link: URL(string: "https://zoom.us/j/1")),
                    ]
                case "plug", "unplug":
                    let on = (n.object as? String) == "plug", pct = self.model.battery?.percent ?? 64
                    self.model.announce(Toast(symbol: on ? "bolt.fill" : "powerplug.portrait", tint: on ? .green : .gray, title: on ? L("Charging") : L("Unplugged"),
                                              subtitle: "\(pct)%", event: on ? .charging : .unplugged, compact: true, art: .charge(percent: pct, plugged: on)), notify: false)
                case "airpods-on", "airpods-off", "mouse-on":
                    let k = n.object as? String ?? "", on = !k.hasSuffix("off"), mouse = k.hasPrefix("mouse")
                    let name = mouse ? "X3 Mouse" : "AirPods Pro"
                    self.model.announce(Toast(symbol: mouse ? "computermouse" : "airpodspro", tint: on ? .blue : .gray, title: name + (on ? L(" connected") : L(" disconnected")),
                                              subtitle: on ? (mouse ? L("Ready") : L("L 100% · R 98% · Case 62%")) : "", event: on ? .deviceOn : .deviceOff,
                                              art: .device(art: mouse ? nil : "banner:Banner-PID-8212", symbol: mouse ? "computermouse" : "airpodspro", connected: on)), notify: false)
                case "guide":
                    if let b = BrowserJS.browser("com.google.Chrome") { BrowserGuide.show(b) {} }
                case "dumpdevices":
                    appLog("devices.log", self.model.devices.map { "\($0.id) art=\($0.art ?? "nil") img=\(DeviceArt.image($0.art) != nil)" }.joined(separator: " | "))
                case "dumpevents":
                    appLog("calendar.log", self.model.events.map { "\($0.allDay ? "ALLDAY" : "timed") \($0.title) \($0.start)–\($0.end) hidden=\(self.model.dismissedEvents.contains($0.id))" }
                        .joined(separator: " | ") + " · modules on=\(self.settings.on(.calendar)) auth=\(CalendarSource.authorized)")
                case "noevent": self.model.debugEvents = false; self.model.dismissedEvents = []; self.model.refreshEvents()
                case "close": self.model.expanded = false
                // sample microphone / camera / call states, in memory only; "nosensor" goes back to the real readings
                case "call": self.model.debugSensors = SensorState(micApps: ["us.zoom.xos"], camera: true)
                case "mic": self.model.debugSensors = SensorState(micApps: ["com.apple.QuickTimePlayerX"])
                case "nosensor": self.model.debugSensors = nil
                // the health warnings without heating the Mac or filling the disk
                case "hot": self.model.thermalChanged(.serious)
                case "hot-critical": self.model.thermalChanged(.critical)
                case "disk": self.model.checkDisk(free: 7_200_000_000); self.model.checkDisk(free: 20_000_000_000)  // warn, then reset the once-only flag
                // what the lock screen notch does on a wrong password, a wrong finger, or Touch ID passing
                case "authfail", "authtouch", "authok":
                    let o = n.object as? String
                    let at = Date()
                    self.model.authFlash = (o == "authok" ? .success : .failed(touchID: o == "authtouch"), at)
                    if o != "authok" { Sounds.play(.authFail) }
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.8) {
                        MainActor.assumeIsolated { if self.model.authFlash?.at == at { self.model.authFlash = nil } }
                    }
                case let o? where o.hasPrefix("pane-") || o.hasPrefix("guide-") || o.hasPrefix("edit-"): break  // the window switches its own page
                case "onboarding": self.showOnboarding()
                // the settings window as tall as the screen, so one screenshot shows a whole pane
                case "settings-tall":
                    self.showSettings()
                    if let w = self.settingsWindow, let v = w.screen?.visibleFrame {
                        w.setFrame(NSRect(x: w.frame.minX, y: v.minY, width: w.frame.width, height: v.height), display: true)
                    }
                // answer the oldest agent ask as its buttons would
                case "ask-allow": if let a = self.model.asks.first { self.model.reply(a, .allow) }
                case "ask-deny": if let a = self.model.asks.first { self.model.reply(a, .deny) }
                case "ask-first":
                    if let a = self.model.asks.first {
                        self.model.reply(a, .answers(Dictionary(uniqueKeysWithValues: a.questions.map { ($0.question, $0.options.first ?? "") })))
                    }
                default: self.model.expanded = true
                }
            }
        }
        // the power button sleeps the display while locked; on wake the lock screen is rebuilt above our window
        for name in [NSWorkspace.screensDidWakeNotification, NSWorkspace.didWakeNotification] {
            NSWorkspace.shared.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { [weak self] n in
                MainActor.assumeIsolated {
                    guard let self, self.isLocked else { return }
                    LockScreenSpace.log("\(n.name.rawValue) while locked")
                    self.screenLocked(true)
                }
            }
        }
        // not every wake is announced (a power-button press can just blank the display), so keep it on top while locked
        Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.isLocked, let p = self.lockPanel else { return }
                p.orderFrontRegardless()
                LockScreenSpace.adopt(p, quiet: true)
            }
        }

        permissions.enableBrowser = { [weak self] bid in self?.model.enableBrowserControl(bid) }
        setUpLockPanel()
        if !settings.onboardingDone { showOnboarding() }
    }

    func applicationWillTerminate(_ notification: Notification) { model.fanControl.stop(model.fans) }

    /// Lives in the lock screen space from launch (moving it in only after locking is too late), invisible until locked.
    func setUpLockPanel() {
        guard settings.lockScreen, lockPanel == nil else { return }
        let p = NotchPanel(contentRect: panel.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.backgroundColor = .clear
        p.isOpaque = false
        p.hasShadow = false
        p.ignoresMouseEvents = true
        p.level = .screenSaver
        p.collectionBehavior = [.stationary, .ignoresCycle]  // not canJoinAllSpaces: that would pull it back out of the space
        let host = NSHostingView(rootView: LocalizedRoot { NotchView(model: self.model, lockScreen: true) })
        host.sizingOptions = []
        p.contentView = host
        p.alphaValue = 0
        p.orderFrontRegardless()
        LockScreenSpace.adopt(p)
        lockPanel = p
    }

    func screenLocked(_ locked: Bool) {
        LockScreenSpace.log(locked ? "locked" : "unlocked")
        let wasLocked = isLocked
        isLocked = locked
        watchUnlocks(locked: locked, wasLocked: wasLocked)
        if locked { model.expanded = false }  // nobody can hover it there
        setUpLockPanel()
        guard let p = lockPanel else { return }
        p.setFrame(panel.frame, display: true)
        p.alphaValue = locked && settings.lockScreen ? 1 : 0
        if locked {
            p.orderFrontRegardless()
            LockScreenSpace.adopt(p)  // again: the space can be dropped or reordered across lock and display sleep
            p.contentView?.needsDisplay = true
        }
        LockScreenSpace.log("lock panel alpha \(p.alphaValue) visible \(p.isVisible) frame \(p.frame)")
    }

    /// Wrong password or finger: the lock screen notch shakes; back at the desktop, say how many tries failed.
    func watchUnlocks(locked: Bool, wasLocked: Bool) {
        guard settings.lockAuth else { authWatch.stop(); return }
        if locked {
            if !wasLocked { model.failedUnlocks = [] }
            authWatch.onEvent = { [weak self] e in
                guard let self else { return }
                let at = Date()
                self.model.authFlash = (e, at)
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.8) {
                    MainActor.assumeIsolated { if self.model.authFlash?.at == at { self.model.authFlash = nil } }
                }
                if case .failed = e {
                    self.model.failedUnlocks.append(Date())
                    Sounds.play(.authFail)
                }
            }
            authWatch.start()
            return
        }
        authWatch.stop()
        model.authFlash = nil
        let fails = model.failedUnlocks
        model.failedUnlocks = []
        guard wasLocked, let last = fails.last else { return }
        model.announce(Toast(symbol: "lock.trianglebadge.exclamationmark.fill", tint: .orange,
                             title: fails.count == 1 ? L("1 failed unlock") : L("{0} failed unlocks", fails.count),
                             subtitle: L("Last at {0} while locked", last.formatted(date: .omitted, time: .shortened)), event: nil))
    }

    /// A `.notchsounds` file opened from Finder: its sounds replace the current ones at once.
    func application(_ sender: NSApplication, open urls: [URL]) {
        for url in urls where url.pathExtension.lowercased() == "notchsounds" {
            let name = url.deletingPathExtension().lastPathComponent
            if let why = settings.importSounds(url) {
                model.announce(Toast(symbol: "speaker.slash.fill", tint: .red, title: L("Couldn't import the sound pack"), subtitle: why, event: nil), notify: false)
            } else {
                // its own "done" sound announces it: you hear the new pack straight away
                model.announce(Toast(symbol: "speaker.wave.2.fill", tint: .green, title: L("Sound pack applied"), subtitle: name, event: .taskDone), notify: false)
            }
        }
    }

    // opening the app again (Finder, Spotlight) shows the control panel
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showSettings()
        return false
    }

    // MARK: Windows

    func showSettings() {
        updater.checkIfDue()
        if settingsWindow == nil {
            let root = SettingsRoot(model: model, permissions: permissions, updater: updater) { [weak self] in self?.showOnboarding() }
            let w = NSWindow(contentViewController: NSHostingController(rootView: LocalizedRoot { root }))
            w.title = "NotchTasks"
            w.styleMask = [.titled, .closable, .resizable, .miniaturizable, .fullSizeContentView]
            w.setContentSize(NSSize(width: 860, height: 620))
            w.isReleasedWhenClosed = false
            w.center()
            settingsWindow = w
        }
        NSApp.activate(ignoringOtherApps: true)  // see showOnboarding: the polite activate() leaves it behind
        settingsWindow?.makeKeyAndOrderFront(nil)
        settingsWindow?.orderFrontRegardless()
    }

    func showOnboarding() {
        if onboardingWindow == nil {
            let view = OnboardingView(model: model, permissions: permissions) { [weak self] action in
                guard let self else { return }
                self.onboardingWindow?.close()
                self.onboardingWindow = nil
                switch action {
                case .close: break
                case .phone:
                    self.showSettings()
                    DispatchQueue.main.async { NotificationCenter.default.post(name: .openPane, object: Pane.phone.rawValue) }
                case .notch: self.model.expanded = true
                }
            }
            let w = NSWindow(contentViewController: NSHostingController(rootView: LocalizedRoot { view }))
            w.title = "NotchTasks"
            w.styleMask = [.titled, .closable, .fullSizeContentView]
            w.titlebarAppearsTransparent = true
            w.isReleasedWhenClosed = false
            w.center()
            onboardingWindow = w
        }
        // an accessory app isn't activated by the polite activate(): without this the guide opens behind other windows,
        // inactive, every switch drawn grey as if off
        NSApp.activate(ignoringOtherApps: true)
        onboardingWindow?.makeKeyAndOrderFront(nil)
        onboardingWindow?.orderFrontRegardless()
    }

    /// A language change: the AppKit menu is built once, so it is built again; SwiftUI views follow LocalizedRoot.
    @objc func languageChanged() {
        guard statusItem != nil else { return }
        updateStatusItem(false)
        updateStatusItem(true)
    }

    func updateStatusItem(_ show: Bool) {
        guard show else {
            if let item = statusItem { NSStatusBar.system.removeStatusItem(item) }
            statusItem = nil
            return
        }
        guard statusItem == nil else { return }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = NSImage(systemSymbolName: "rectangle.topthird.inset.filled", accessibilityDescription: "NotchTasks")
        let menu = NSMenu()
        menu.addItem(ClosureMenuItem(L("Control Panel…"), "slider.horizontal.3") { [weak self] in self?.showSettings() })
        menu.addItem(ClosureMenuItem(L("Guide…"), "book") { [weak self] in self?.showOnboarding() })
        menu.addItem(.separator())
        for m in [5, 15, 25, 45] {
            menu.addItem(ClosureMenuItem(L("{0}-minute timer", m), "timer") { [weak self] in self?.model.startTimer(minutes: m) })
        }
        menu.addItem(ClosureMenuItem(L("Stop timer"), "stop.circle") { [weak self] in self?.model.stopTimer() })
        menu.addItem(.separator())
        menu.addItem(ClosureMenuItem(L("Quit NotchTasks"), "power") { NSApp.terminate(nil) })
        item.menu = menu
        statusItem = item
    }

    // MARK: Notch geometry & mouse

    func layout() {
        guard let s = NSScreen.screens.first(where: { $0.safeAreaInsets.top > 0 }) ?? NSScreen.main else { return }
        screen = s
        var notch = CGSize(width: 200, height: NSStatusBar.system.thickness)
        // debugNoNotch: lets a notched Mac show the no-notch layout, for checking it by screenshot
        let hasNotch = s.safeAreaInsets.top > 0 && s.auxiliaryTopLeftArea != nil && !UserDefaults.standard.bool(forKey: "debugNoNotch")
        if hasNotch, let l = s.auxiliaryTopLeftArea, let r = s.auxiliaryTopRightArea {
            notch = CGSize(width: s.frame.width - l.width - r.width, height: s.safeAreaInsets.top)
        }
        // no camera housing (older Macs, external displays): a floating pill reads better than a black tab over the menu bar
        let island = !hasNotch
        model.hasNotch = hasNotch
        model.island = island
        model.islandTop = island ? (hasNotch ? notch.height + 4 : 4) : 0
        // no camera to leave room for: the middle gap shrinks to a hairline
        model.notch = island ? CGSize(width: 12, height: 30) : hasNotch ? notch : CGSize(width: 12, height: notch.height)
        let size = CGSize(width: 720, height: max(760, ceil(model.contentSize.height + model.islandTop)))
        panel.setFrame(NSRect(x: s.frame.midX - size.width / 2, y: s.frame.maxY - size.height, width: size.width, height: size.height), display: true)
    }

    private func notchRect(_ s: NSScreen) -> NSRect {
        let size = model.contentSize
        let h = size.height + model.islandTop  // the gap above a floating pill still counts, so the screen edge opens it
        return NSRect(x: s.frame.midX - size.width / 2, y: s.frame.maxY - h, width: size.width, height: h)
            .insetBy(dx: -4, dy: -4)
    }

    func trackMouse(_ type: NSEvent.EventType) {
        if type == .leftMouseUp {
            if model.dragging { DispatchQueue.main.async { [weak self] in self?.model.dragging = false } }
            return  // a click is not a pointer exit; check again when a drag ends
        }
        guard let s = screen else { return }
        let inside = notchRect(s).contains(NSEvent.mouseLocation)

        if model.expanded {
            if inside || model.dragging || model.presenting { collapseWork?.cancel(); collapseWork = nil; return }
            guard collapseWork == nil else { return }
            let work = DispatchWorkItem { [weak self] in
                MainActor.assumeIsolated {
                    guard let self, let s = self.screen else { return }
                    self.collapseWork = nil
                    guard self.model.expanded, !self.notchRect(s).contains(NSEvent.mouseLocation),
                          !self.model.dragging, !self.model.presenting else { return }
                    self.hoverWork?.cancel()
                    self.hoverWork = nil
                    self.model.expanded = false
                    self.panel.ignoresMouseEvents = true
                }
            }
            collapseWork = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: work)
            return
        }
        panel.ignoresMouseEvents = !inside  // clickable while the pointer is over the notch (click mode)
        model.toastHovered = inside && model.toast != nil
        guard inside else {
            hoverWork?.cancel()
            hoverWork = nil
            return
        }
        // files dragged onto the notch open it at once; otherwise follow the user's open mode
        let draggingIn = type == .leftMouseDragged
        // over a toast the pointer is there to read or click it, not to open the notch (a dragged file still opens it)
        guard model.toast == nil || draggingIn else { return }
        guard settings.expandMode == .hover || draggingIn, hoverWork == nil else { return }
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.hoverWork = nil
                if self.notchRect(s).contains(NSEvent.mouseLocation), self.model.toast == nil || draggingIn { self.model.expanded = true }
            }
        }
        hoverWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + (draggingIn ? 0 : settings.hoverDelay), execute: work)
    }
}
