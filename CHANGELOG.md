# Changelog

## 1.7.0 — new icon
- **New app icon** in the iOS 26 / macOS 26 Liquid Glass style: a dark plum tile with a frosted glass frame, a glowing neon stroke drawn by a marker, a red recording dot and an A marker — capture, recording, drawing and markers in one picture.
- Every size is drawn from vector: a simplified version for 32 px and below (taskbar, tray), the Apple icon grid with margins for macOS, and a larger tile for Windows and Linux so it matches neighbouring icons.
- Icon sources (SVG) and the export script live in `packaging/icon/`.

## 1.6.0 — typography
- **Apple type scale.** All text now follows one scale based on Apple's macOS text styles (body 13 pt): Large Title 26, Title 1 22, Title 2 17, Title 3 15, Headline 13, Body 13, Callout 12, Footnote 12, Caption 11. Buttons, fields and lists use the same body size as labels (they used to be 14).
- **Optical sizes, like San Francisco.** On Windows SnapMark uses Segoe UI Variable with its three optical sizes: Display for titles from 20 pt, Text for regular text, Small for tiny labels. macOS uses San Francisco; Linux uses Inter, Ubuntu or Cantarell.
- **Tracking and leading.** Large text is set slightly tighter and small text slightly looser, as in SF; wrapped descriptions get even line spacing. Settings section titles are stronger, and the recording timer uses the system font.
- **System text size.** The Windows setting Accessibility → Text size now enlarges SnapMark's text too (up to 140 %), like Dynamic Type.

## 1.5.3
- **Glass appearance.** New option in Settings → General → Glass appearance: *Auto* (light glass over light content, dark over dark — as before), *Always light* or *Always dark*.
- **Fixed: bars overlapping on small selections.** For a small capture area the tool bar is wider than the selection and used to run under the action bar. The action bar now looks for a free spot next to the selection, and the tool bar moves aside (or below the action bar) when space is tight.

## 1.5.2
- **Fixed: no frost on Windows.** With hardware (GPU) rendering, the glass shader drew the background shifted or transparent, so panels showed sharp content behind a dark tint. The frosted background is now always drawn with a plain image brush, and the shader only adds lensing and highlights at the edges, computing its own coordinates.
- Glass is now also checked in CI in a real Windows window with GPU rendering (ANGLE).
- Setting the environment variable `SNAPMARK_GLASS_NOSHADER=1` turns off the edge shader if a graphics driver misbehaves.

## 1.5.1
- **Fixed: the glass was never frosted.** Since 1.3.0 the blurred background under the glass was silently not created (Avalonia can't resize the bitmap type SnapMark uses for screenshots), so panels were only tinted and the Frost and Transparency sliders seemed to do nothing. Backdrops are now built with Skia: frost, adaptive light/dark glass and the sliders work as intended.
- Glass panels also pick up a background that appears after they're shown, and glass errors are written to the log instead of being swallowed.
- Glass rendering is now checked in CI with a headless render of real SnapMark panels.

## 1.5.0 — real Liquid Glass optics
- **Frost from clear to fully frosted.** New slider in Settings → General → Glass frost: at 0 % the background under the glass stays sharp, at 100 % it's fully frosted with a subtle grain, like real frosted glass. (System "Reduce transparency" makes glass fully frosted, as in iOS.)
- **Lensing at the edges.** As Apple describes it, Liquid Glass bends light rather than just blurring it: near the edges of every panel and button bar the content behind is pulled in from beyond the border and curves around the glass, with a hint of dispersion right at the rim. Larger panels bend light more, small ones more gently.
- **Specular highlights** that follow the shape: the edges facing the light (top-left) glow, the opposite edge gets a faint reflection.
- **Adaptive shadow:** denser over text and busy content, lighter over plain bright backgrounds.
- Lensing needs GPU rendering (the default on Windows and macOS). With software rendering SnapMark keeps the frost and simply skips the bending, so it can never crash.

## 1.4.1
- **Morphing selection lens.** In the editor toolbar and on the recording bar the glass lens no longer just jumps: when you pick another tool it glides over to it with a light spring and stretches along the way like a drop of liquid glass, as in iOS / macOS 26. With "Reduce motion" (macOS) or Windows animations turned off it moves instantly.

## 1.4.0 — Liquid Glass, by Apple's rules
- **Adaptive glass (Regular):** each panel looks at what's under it — over a bright page it turns light with dark text, over a dark one it turns dark with light text, so everything stays readable over any screenshot.
- **Clear glass** — a second style for bright backgrounds and video: almost transparent, with a light dimming layer and always-light content. Settings → General → Glass style. One style for the whole app, as Apple recommends.
- **Apple's shape system:** toolbars, the recording and series bars are capsules; tool buttons, chips and buttons are capsules too; nested elements are concentric with their panel.
- **No decorated bars:** groups inside glass are separated by space instead of lines; the current color is marked with a neutral ring — color is reserved for primary actions (Start recording, Stop).
- **Press feedback:** tool buttons gently press in when clicked, like interactive glass.
- **System accessibility:** with "Transparency effects" off (Windows) or "Reduce transparency" (macOS) the glass becomes frosted without blur; with High contrast / Increase contrast panels become solid with a contrasting edge.

## 1.3.0 — Liquid Glass
- **New design in the style of iOS / macOS 26 Liquid Glass.** Toolbars, style panels, the recording bar, notifications and the series panel are now frosted glass: the screenshot (or, on Windows, whatever is behind a floating panel) shows through blurred, with a soft tint, a highlight at the top and a thin bright rim.
- The selected tool, switched-on chips and the current Settings section are a glass "lens" with a highlight instead of a solid blue fill. Buttons, fields, drop-down lists and tooltips are translucent with larger rounded corners.
- **Glass transparency** slider in Settings → General: from almost frosted to almost clear; changes instantly.
- The Settings window uses the system Mica / Acrylic glass on Windows 11/10 (and translucent cards).

## 1.2.5
- Hidden mouse cursor now stays hidden in the video after you pick any drawing tool. On Windows the layer with SnapMark's own cursor and the selection frame could stop being hidden from the recording once it was made click-through; it now passes clicks differently, and SnapMark checks that the layer is really excluded before drawing its cursor there.

## 1.2.4
- Mouse cursor off really means no cursor in the video: while drawing, the crosshair, text cursor and other drawing cursors no longer get into the recording either. You still see them — SnapMark draws its own cursor on a layer that's hidden from the recording (Windows 10 2004+, macOS).

## 1.2.3
- Mouse cursor in recordings: show or hide it — in the recording options (Mouse cursor) and in Settings → Video. Works on Windows, macOS (also hides click highlights) and Linux.
- Drawing on video: fixed a freeze that could happen the first time you started typing text. The drawing layer now takes the keyboard with a normal activation instead of attaching to another app's input thread.

## 1.2.2
- Drawing on video: the Style panel's buttons (colors, shadow, frame, font…) now always work. Before, while typing text the drawing layer could cover the panel, and clicks went through it and created new text instead.

## 1.2.1
- Drawing on video now has every editor tool: pencil (C), line (L) and double arrow (D) joined the pen, highlighter, arrow, shapes, markers, text and eraser.
- A B C / 1 2 3 switch for markers on video (in the Style panel) — relabels all markers at once and is remembered, just like in the editor.

## 1.2.0
- **Full editor styles while recording.** New Select tool (V): move objects, double-click a text or a marker to edit it, Delete to remove, Ctrl+J to duplicate. The Style button opens the same settings as in the screenshot editor — color, thickness, opacity, dashes, fill, font, weight, italic, size, frame and plate colors, shadows, marker shapes and comment-label style — for the selected object, the text you're typing, or new objects.
- **Only your drawings get into the video.** The recording bar, the style panel, tooltips, drop-down lists and the selection frame are excluded from the recording (Windows 10 2004+ and macOS; on Linux the system can't hide windows from a recording).
- **Video quality:** Low, Standard, High (crisp small text) or Maximum — in the recording options and in Settings → Video. Works for MP4, MKV, WebM and GIF.
- **Exact recording size:** type width × height or pick 1280×720, 1920×1080, 1080×1080 or 1080×1920 (vertical for Reels/Shorts/TikTok); the area keeps its center and stays on screen.
- The Mouse button (Esc) now has its own icon: clicks go through the drawings to your apps.

## 1.1.1
- Drawing on video: markers with and without an arrow are now separate tools, as in the screenshot editor. The plain marker (M) is placed exactly where you click and never gets an accidental arrow; the arrow marker (N) — press and drag to the target.

## 1.1.0
- **Draw on the video while recording.** Press the pen button on the recording bar: pen, highlighter, arrow, rectangle, ellipse, A-B-C markers (click, or drag to point at something, then type a comment), text and eraser, 7 colors, undo and "erase all". Everything appears in the video as you draw and type.
- The cursor button (or Esc) lets clicks go through the drawings to your apps, so you can keep working while the drawings stay on screen.
- Hotkeys while drawing: P pen, H highlighter, A arrow, R rectangle, O ellipse, M marker, N marker with arrow, T text, E eraser, Ctrl+Z / Ctrl+Y undo / redo; hold the pen still at the end of a stroke to turn it into a clean shape; Shift — straight lines and squares.

## 1.0.9
- Notifications (copied, saved, recording finished…) now appear in the bottom-right corner just above the taskbar / Dock, so they no longer cover window buttons like Minimize and Close.
- Notifications are more compact: smaller card, icon and text.

## 1.0.8
- Moving the series panel no longer writes settings to disk on every pixel — the position is saved once the panel stops.
- Linux: standard file permissions inside the AppImage and .deb (fixes 200+ packaging warnings); proper maintainer and homepage in the .deb.

## 1.0.7
- Pin marker: smoother, symmetric teardrop shape (classic map-pin look).

## 1.0.6
- Marker tool icons: letter A with rounded strokes, matching the Text tool icon.

## 1.0.5
- Marker tool icons: wider, iOS-style letter A.

## 1.0.4
- Redesigned icon set in an iOS-like style: thin even strokes, rounded corners and caps, consistent 20-pt grid — tools, shapes, actions, recording and settings.

## 1.0.3
- Multi-line marker comments: Shift+Enter starts a new line (Enter still finishes the comment). Line breaks are kept on the image and in the copied text.
- New marker tool icons: a solid badge with a cut-out letter, perfectly centered and crisp at any scale.

## 1.0.2
- Marker comments drawn on the image no longer cover the marker's arrow, other markers or other comments — the best free spot around the marker is chosen automatically.
- The style panel next to an object no longer covers the editor's side panels (actions, comments, tools).
- The series panel can be moved: drag it by the ⋮⋮ grip or any free spot; the position is remembered. Double-click the grip to put it back.

## 1.0.1
- In-app update check: SnapMark tells you when a new version is out (Settings → About, can be turned off).

## 1.0.0 — first public release
- Screenshots of a region or full screen, multi-monitor, custom hotkeys.
- Markers A-B-C / 1-2-3 with comments, copied as text and optionally drawn on the image.
- Pen, pencil, highlighter, eraser, shapes with hold-to-snap, text with frames and shadows, per-object style panels.
- Series mode and smart paste (images + text in one Ctrl+V).
- Screen recording to MP4 / MKV / WebM / GIF with microphone and computer audio.
- Windows, macOS (Apple Silicon & Intel), Linux.
- English, Russian, German, French, Italian, Spanish.
