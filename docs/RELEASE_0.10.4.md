# F1 Telemetry Lab v0.10.4

## Overlay input hotfix

The locked Race Engineer overlay now uses two independent Windows input pass-through mechanisms:

- `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` window styles;
- `HTTRANSPARENT` for `WM_NCHITTEST` while layout editing is disabled.

Changing the layout back to edit mode removes the transparent and no-activate styles and immediately refreshes the native window frame. The overlay therefore remains interactive while editing and passes mouse input to the game after `Lock`.

No ERS strategy or China profile parameters changed in this patch.
