import AppKit

/// Sitch, the screenshot editor (github.com/flexlm307/Sitch). "Edit in Sitch" hands it a photo
/// with `--edit`: Save in Sitch writes back into the same file, and the card shows the new
/// version. A running Sitch gets the path over the distributed notification its own second
/// launches use; otherwise the app is opened with the argument.
@MainActor
enum Sitch {
    static let bundleID = "io.github.phalladar.Sitch"
    private static let forwardArgs = Notification.Name("io.github.phalladar.Sitch.forwardArgs")
    private static let settingsFile = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Sitch/settings.json")

    /// Called when a photo handed to Sitch has been saved over.
    static var onSaved: (URL) -> Void = { _ in }

    private static var running: NSRunningApplication? {
        NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).first
    }
    private static var appURL: URL? { NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) }

    /// Running, or installed: the menu offers "Edit in Sitch".
    static var available: Bool { running != nil || appURL != nil }

    /// Sitch has been on this Mac at least once.
    static var installed: Bool { available || FileManager.default.fileExists(atPath: settingsFile.path) }

    @discardableResult
    static func edit(_ url: URL) -> Bool {
        let arg = "--edit=" + url.standardizedFileURL.path
        if let app = running {
            // Let Sitch bring its editor to the front.
            NSApp.yieldActivation(to: app)
            DistributedNotificationCenter.default().postNotificationName(
                forwardArgs, object: nil, userInfo: ["args": [arg], "pid": Int(app.processIdentifier)],
                deliverImmediately: true)
        } else if let appURL {
            let config = NSWorkspace.OpenConfiguration()
            config.arguments = [arg]
            NSWorkspace.shared.openApplication(at: appURL, configuration: config) { _, error in
                if let error { log.error("Could not open Sitch: \(error.localizedDescription, privacy: .public)") }
            }
        } else {
            return false
        }
        watch(url)
        return true
    }

    /// Where Sitch saves captures: its chosen folder, or Pictures/Sitch.
    static func saveFolder() -> URL {
        if let data = try? Data(contentsOf: settingsFile),
           let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let folder = json["defaultSaveFolder"] as? String, !folder.isEmpty {
            let path = (folder as NSString).expandingTildeInPath
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue {
                return URL(fileURLWithPath: path, isDirectory: true)
            }
        }
        return FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Sitch", isDirectory: true)
    }

    // MARK: Noticing the save

    /// Photos handed to Sitch, with the time each was last written. Checked every second for
    /// an hour: the screenshot folder watcher only reports new files, not changed ones.
    private static var watching: [URL: (written: Date?, until: Date)] = [:]
    private static var timer: Timer?

    private static func watch(_ url: URL) {
        watching[url] = (modified(url), Date().addingTimeInterval(3600))
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { _ in
            MainActor.assumeIsolated { check() }
        }
    }

    private static func check() {
        let now = Date()
        for (url, entry) in watching {
            guard now < entry.until, FileManager.default.fileExists(atPath: url.path) else {
                watching[url] = nil
                continue
            }
            let written = modified(url)
            if written != entry.written {
                watching[url] = (written, entry.until)
                onSaved(url)
            }
        }
        if watching.isEmpty {
            timer?.invalidate()
            timer = nil
        }
    }

    private static func modified(_ url: URL) -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date
    }
}
