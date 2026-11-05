# Changelog

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
