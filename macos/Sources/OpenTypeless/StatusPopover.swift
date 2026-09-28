import AppKit
import SwiftUI

/// The menu-bar popover.
///
/// The SwiftUI content is measured and the popover pinned to that size *before* it is shown, and the
/// hosting controller never resizes it afterwards. Letting NSHostingController grow the popover while it
/// was already on screen made it re-anchor mid-animation: it ended up shifted off the status item,
/// with its top pushed past the top of the screen.
@MainActor
final class StatusPopover {
    private let popover = NSPopover()
    private let host: NSHostingController<MenuPopoverView>
    static let width: CGFloat = 340

    var isShown: Bool { popover.isShown }
    var window: NSWindow? { host.view.window }

    init(rootView: MenuPopoverView) {
        host = NSHostingController(rootView: rootView)
        host.sizingOptions = []
        popover.behavior = .transient
        popover.animates = true
        popover.contentViewController = host
    }

    func show(from button: NSStatusBarButton) {
        popover.contentSize = fittingSize()
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        host.view.window?.makeKey()
    }

    func close() {
        popover.performClose(nil)
    }

    private func fittingSize() -> NSSize {
        let size = host.sizeThatFits(in: NSSize(width: Self.width, height: 10_000))
        // Never taller than the space under the menu bar.
        let maxHeight = (NSScreen.main?.visibleFrame.height ?? 900) - 40
        return NSSize(width: Self.width, height: min(ceil(size.height), maxHeight))
    }
}
