# F1 Telemetry Lab v0.10.5

## Overlay focus hotfix

The Race Engineer overlay is now shown as an independent top-level window instead of a window owned by the main application. This prevents Windows from lowering or hiding the overlay when focus moves from F1 Telemetry Lab to a borderless or windowed F1 session.

When the overlay loses focus in locked mode, the application reapplies `HWND_TOPMOST` with `SWP_NOACTIVATE`. The game keeps keyboard and controller focus while the overlay remains visible and mouse-transparent.

Exclusive full-screen rendering can bypass Windows desktop composition and remains unsupported. Use Borderless Windowed or Windowed mode for overlays.

ERS strategy and China profile revision 4 are unchanged.
