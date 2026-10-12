import AppKit
import Combine
import SwiftUI

/// The menu-bar popover.
///
/// The SwiftUI content is measured and the popover pinned to that size *before* it is shown, and the
/// hosting controller never resizes it on its own. Letting NSHostingController grow the popover while it
/// was already on screen made it re-anchor mid-animation: it ended up shifted off the status item,
/// with its top pushed past the top of the screen. What it shows can still change while it's open (a dictation
/// fails, an update arrives, a permission is granted), so once it has finished opening it's measured again then.
@MainActor
final class StatusPopover: NSObject, NSPopoverDelegate {
    private let popover = NSPopover()
    private let host: NSHostingController<MenuPopoverView>
    private weak var button: NSStatusBarButton?
    /// Shown and done animating in, so a new size can't knock it off the status item.
    private var settled = false
    private var changes: AnyCancellable?
    static let width: CGFloat = 340

    var isShown: Bool { popover.isShown }
    var window: NSWindow? { host.view.window }

    init(rootView: MenuPopoverView) {
        host = NSHostingController(rootView: rootView)
        super.init()
        host.sizingOptions = []
        popover.behavior = .transient
        popover.animates = true
        popover.contentViewController = host
        popover.delegate = self
        // Everything the popover shows. A dictation updates its record several times, so a burst is measured once.
        changes = Publishers.MergeMany(
            rootView.controller.objectWillChange.map { _ in () }.eraseToAnyPublisher(),
            rootView.settings.objectWillChange.map { _ in () }.eraseToAnyPublisher(),
            rootView.history.objectWillChange.map { _ in () }.eraseToAnyPublisher(),
            rootView.updater.objectWillChange.map { _ in () }.eraseToAnyPublisher(),
            rootView.permissions.objectWillChange.map { _ in () }.eraseToAnyPublisher()
        )
        .debounce(for: .milliseconds(100), scheduler: RunLoop.main)
        .sink { [weak self] in self?.refit() }
    }

    func show(from button: NSStatusBarButton) {
        self.button = button
        PermissionsMonitor.shared.setWatching(true, by: .popover)
        popover.contentSize = fittingSize()
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        host.view.window?.makeKey()
    }

    func close() {
        popover.performClose(nil)
    }

    func popoverDidShow(_ notification: Notification) {
        settled = true
        refit()
    }

    func popoverDidClose(_ notification: Notification) {
        settled = false
        PermissionsMonitor.shared.setWatching(false, by: .popover)
    }

    /// Grows or shrinks the open popover to its content, so a banner that appears doesn't push the footer out of it.
    private func refit() {
        guard settled, popover.isShown else { return }
        let size = fittingSize()
        if size != popover.contentSize { popover.contentSize = size }
    }

    private func fittingSize() -> NSSize {
        let size = host.sizeThatFits(in: NSSize(width: Self.width, height: 10_000))
        // Never taller than the space under the menu bar of the screen the icon is on.
        let screen = button?.window?.screen ?? NSScreen.main
        let maxHeight = (screen?.visibleFrame.height ?? 900) - 40
        return NSSize(width: Self.width, height: min(ceil(size.height), maxHeight))
    }
}
