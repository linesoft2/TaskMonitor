# The scrolling stack

Three files, one design. **`src/UI/Common/TouchDragScrollViewer.cs` is the single origin**
— its class comment carries the full rationale; `SnappyScrollPhysics.cs` and
`ScrollCacheDuringTouch.cs` document only what is unique to them and point back here.

| File | Role |
|---|---|
| `TouchDragScrollViewer.cs` | `FluentWpfCore.SmoothScrollViewer` subclass: the touch-drag gesture (延迟捕获), the `MaxOffset` push, the scrollbar binding dance |
| `SnappyScrollPhysics.cs` | `IScrollPhysics` replacement: library-equivalent feel + two bugfixes |
| `ScrollCacheDuringTouch.cs` | Attached behavior: windowed `BitmapCache` during touch drags only |

**Where it is used:** ONE settings page + the five detail views' process lists. All six
scrollers are `TouchDragScrollViewer`, all six set `Physics` to `SnappyScrollPhysics`, and
all six set `ScrollCacheDuringTouch.Enabled="True"`.

## Why it exists at all

`SmoothScrollViewer` is v3 architecture — visual/logical separation: `RenderTransform`
scrolls at full frame rate while the logical offset syncs infrequently. We keep it (and
keep the library's default *feel*) for **two bugs in 1.0.5** that this subclass fixes:

1. **Precise mode `IsStable` is never true** in the library — the render loop can only stop
   by hitting a boundary. Scrolling to the middle of a page with a touchpad then left the
   content with `IsHitTestVisible=false`, freezing every click. Fixed in
   `SnappyScrollPhysics`: stable means "remaining < `StopDistance` AND input quiet >
   `PreciseIdleSeconds`".
2. **Out-of-range accumulation** — scroll past the bottom/top and the remaining distance
   piles up outside the range; the reverse scroll must "pay it back" before anything moves
   (the library's default physics has the same disease). Fixed by clamping every frame's
   target into `[0, MaxOffset]`, with `MaxOffset` pushed from
   `TouchDragScrollViewer.OnScrollChanged`'s `ScrollableHeight`.

**The rate constants are strictly equivalent to the library defaults** (wheel k≈9.55/s,
precise k≈18.4/s; `k = -144·ln(friction)`), because the user explicitly asked for the
default feel back after trying faster values. Only the two bugfixes deviate. Do not
"tune" them.

## Touch drag = 延迟捕获 (delayed capture)

Both off-the-shelf routes are unusable, each for a measured reason:

- **The library's manipulation** (`IsEnableSmoothManipulating`) captures the touch point in
  `TouchDown`, so a tap no longer promotes to a mouse click and every switch/button inside
  the page stops responding to touch (2026-08-01).
- **Native `PanningMode`** keeps taps working, but it is layout-driven: every touch move
  (90–120Hz) triggers a full page layout, dropping frames on weak-GPU touch machines.
  `BitmapCache` removed the per-frame repaint cost and the stutter **remained** (2026-08-02
  log: cache engaged, still janky).

The adopted flow:

1. `TouchDown` only **observes** — no handling, no capture, so a tap promotes to a click
   normally. A gesture starting on the ScrollBar is not tracked at all; the bar keeps its
   native promotion/drag.
2. Past the threshold, capture the touch point — and balance the `MouseLeftButtonDown` the
   first `TouchDown` already promoted with `Mouse.Capture(null)` (losing capture drops the
   pressed state and suppresses the Click) — then feed the move deltas straight to physics
   as `AnimatedScrollToVerticalOffset(VerticalOffset - dy, true)`, delivered per frame, no
   accumulated error.
3. On release, if the sampled velocity is high enough, run the inertia decay loop
   (`v *= e^(-k·dt)`); it stops at a boundary, at low speed, or on a new press.

## Known tradeoffs (do not treat as bugs)

- A drag starting on a ComboBox opens its dropdown first, and on a TextBox it focuses first
  — the drag aborts at that point; the control does not get stuck. Same as native
  `PanningMode`.
- While physics is rendering, content has `IsHitTestVisible=false`, so a tap within ~0.1s
  after the scroll visually settles is swallowed. Hover/tooltips pause during scrolling.

## The scrollbar / style trap

`TouchDragScrollViewer` does **NOT** inherit iNKORE's implicit `ScrollViewer` style: WPF
keyless styles match `TargetType` exactly and do not reach derived controls. With no `Style`
set, `Style == null` (verified by dump) and the control falls back to the classic Aero
scrollbar, which does not auto-hide.

Therefore all six scrollers set:

```xml
Style="{DynamicResource {x:Type ScrollViewer}}"
```

`DynamicResource` (not `StaticResource`) so it follows light/dark scheme switches. The
template then still contains `PART_VerticalScrollBar`/`PART_HorizontalScrollBar`, which the
control relies on: during scrolling it clears the scrollbars' `Value` bindings and assigns
values manually, then rebuilds them once the scroll settles.

## The BitmapCache window

Never leave `BitmapCache` on permanently — hover/expand/switch animations would
re-rasterize the whole page every frame, which is *worse*. The attached behavior makes it a
window: threshold-crossing drag turns it on (a light tap pays no rasterization cost),
inertia keeps it alive via `ScrollChanged`, and 600ms of silence restores the original
`CacheMode`.

Subscription **must** use `handledEventsToo` — `TouchDragScrollViewer`'s drag marks
move/up as handled and class handlers run before instance handlers, so without the flag the
behavior never sees the events and the cache never engages.
