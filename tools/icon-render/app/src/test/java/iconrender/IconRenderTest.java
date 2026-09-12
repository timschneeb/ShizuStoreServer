package iconrender;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.Drawable;
import android.view.View;
import app.cash.paparazzi.Paparazzi;
import org.junit.Rule;
import org.junit.Test;

/** Renders one drawable resource to an exact-size PNG. Plain drawables and
 *  adaptive-icon layers all draw full-bleed into a fixed square: LayoutLib
 *  drawables do not reliably honor setBounds (vectors render at intrinsic
 *  size), so each layer is scaled by its intrinsic dimensions instead, and
 *  the view is drawn onto its own bitmap rather than trusting Paparazzi's
 *  (screen-sized, downscaled) snapshot. AdaptiveIconDrawable itself cannot
 *  inflate under LayoutLib because the device mask string only exists on a
 *  real launcher, and we want the unmasked square art anyway. Every layer
 *  still renders through real Android drawables. */
public class IconRenderTest {
  @Rule
  public Paparazzi paparazzi = new Paparazzi();

  @Test
  public void render() throws Exception {
    String batch = System.getProperty("iconBatch", "");
    if (!batch.isEmpty()) {
      renderBatch(batch);
      return;
    }
    String name = System.getProperty("iconName", "spike_vector");
    int sizePx = Integer.parseInt(System.getProperty("iconPx", "432"));
    String outPath = System.getProperty("iconOut", "");
    Context context = paparazzi.getContext();
    View root = buildView(context, name, null, sizePx);
    writeExactPng(root, sizePx, outPath);
    paparazzi.snapshot(root, "icon");
  }

  /** Batch mode: one Gradle invocation renders many icons. The manifest
   *  lists {@code drawableName|rootFile|outPath} per line (rootFile empty
   *  for plain drawables); all staged res dirs were merged into the test
   *  resources beforehand, so names must already be unique. No snapshot
   *  call: outputs go straight to their out paths, and a per-entry failure
   *  is logged without aborting the batch (the caller treats a missing
   *  output as a per-icon fallback, not a batch failure). */
  private void renderBatch(String manifestPath) throws Exception {
    int sizePx = Integer.parseInt(System.getProperty("iconPx", "432"));
    Context context = paparazzi.getContext();
    java.util.List<String> lines =
        java.nio.file.Files.readAllLines(java.nio.file.Paths.get(manifestPath));
    int done = 0;
    for (String line : lines) {
      if (line.trim().isEmpty()) {
        continue;
      }
      String[] parts = line.split("\\|", -1);
      if (parts.length != 3) {
        System.out.println("DEBUG batch: skipping malformed line: " + line);
        continue;
      }
      String rootFile = parts[1].isEmpty() ? null : parts[1];
      try {
        View root = buildView(context, parts[0], rootFile, sizePx);
        writeExactPng(root, sizePx, parts[2]);
        done++;
      } catch (Exception e) {
        System.out.println("DEBUG batch: " + parts[0] + " failed: " + e);
      }
    }
    System.out.println("DEBUG batch: rendered " + done + " of " + lines.size());
  }

  private static View buildView(Context context, String name, String rootFile, int sizePx)
      throws Exception {
    int id = context.getResources().getIdentifier(name, "drawable", "iconrender");
    if (id == 0) {
      throw new IllegalArgumentException("Unknown drawable: " + name);
    }
    System.out.println("DEBUG rendering drawable: " + name + " at " + sizePx + "px"
        + " resolved id: " + Integer.toHexString(id));

    View root;
    Drawable plain = null;
    try {
      plain = context.getDrawable(id);
    } catch (RuntimeException e) {
      // AdaptiveIconDrawable cannot inflate under LayoutLib: its
      // constructor parses a device mask string that only exists on a real
      // launcher. It is the only drawable type that fails here, and we
      // want the unmasked square art anyway.
      System.out.println("DEBUG drawable did not inflate (" + e.getClass().getName()
          + ": " + e.getMessage() + "); treating as adaptive-icon");
    }
    if (plain != null) {
      System.out.println("DEBUG plain drawable class: " + plain.getClass().getName()
          + " intrinsic=" + plain.getIntrinsicWidth() + "x" + plain.getIntrinsicHeight());
      root = new FixedDrawView(context, plain, sizePx);
    } else {
      LayerResult bg = layerFromRootFile(context, rootFile, name, sizePx, "background");
      LayerResult fg = layerFromRootFile(context, rootFile, name, sizePx, "foreground");
      System.out.println("DEBUG adaptive bg: " + bg.drawable.getClass().getName()
          + " fg: " + fg.drawable.getClass().getName()
          + " fgIntrinsic=" + fg.drawable.getIntrinsicWidth() + "x" + fg.drawable.getIntrinsicHeight()
          + " fgInset=" + fg.insetLeft + "," + fg.insetTop + "," + fg.insetRight + "," + fg.insetBottom);
      root = new LayersView(context, bg.drawable, fg, sizePx);
    }
    return root;
  }

  /** Exact-size output: measure, lay out and draw onto our own bitmap.
   *  Paparazzi's snapshot file is screen-sized and downscaled, so the C#
   *  side cannot crop the icon square back out of it reliably. */
  private static void writeExactPng(View root, int sizePx, String outPath) throws Exception {
    int spec = View.MeasureSpec.makeMeasureSpec(sizePx, View.MeasureSpec.EXACTLY);
    root.measure(spec, spec);
    root.layout(0, 0, sizePx, sizePx);
    Bitmap bitmap = Bitmap.createBitmap(sizePx, sizePx, Bitmap.Config.ARGB_8888);
    bitmap.eraseColor(Color.TRANSPARENT);
    root.draw(new Canvas(bitmap));
    if (!outPath.isEmpty()) {
      try (java.io.FileOutputStream fos = new java.io.FileOutputStream(outPath)) {
        if (!bitmap.compress(Bitmap.CompressFormat.PNG, 100, fos)) {
          throw new IllegalStateException("Bitmap.compress returned false for " + outPath);
        }
      }
      System.out.println("DEBUG wrote direct png: " + outPath);
    }
  }

  /** Reads the staged text XML for an adaptive-icon layer and resolves its
   *  android:drawable. The adaptive root is NOT a linked resource: Paparazzi
   *  pre-parses every res XML and fails the render on AdaptiveIconDrawable
   *  (no device mask string off-device), so the C# side stages that one
   *  file beside res/ and only the layers resolve as resources. A null
   *  rootFile keeps the old single-render derivation (sibling of the
   *  iconResDir res tree); batch entries always pass the explicit path.
   *  An {@code <inset>} wrapper is preserved with its padding: launchers
   *  honor it (a 24dp inset on the 108dp viewport centers the layer at
   *  60dp), so the store render must too. */
  private static LayerResult layerFromStagedFile(Context context, String name,
      int sizePx, String tag) throws Exception {
    return layerFromRootFile(context, null, name, sizePx, tag);
  }

  /** A resolved layer plus its padding as fractions of the output square. */
  static final class LayerResult {
    final Drawable drawable;
    final float insetLeft;
    final float insetTop;
    final float insetRight;
    final float insetBottom;

    LayerResult(Drawable drawable) {
      this(drawable, 0, 0, 0, 0);
    }

    LayerResult(Drawable drawable, float left, float top, float right, float bottom) {
      this.drawable = drawable;
      this.insetLeft = left;
      this.insetTop = top;
      this.insetRight = right;
      this.insetBottom = bottom;
    }
  }

  private static LayerResult layerFromRootFile(Context context, String rootFile,
      String name, int sizePx, String tag) throws Exception {
    java.io.File xml;
    if (rootFile != null) {
      xml = new java.io.File(rootFile);
    } else {
      String resDir = System.getProperty("iconResDir", "");
      if (resDir.isEmpty()) {
        throw new IllegalStateException("iconResDir sysprop not set");
      }
      xml = new java.io.File(new java.io.File(resDir).getParentFile(), name + ".xml");
    }
    if (!xml.isFile()) {
      throw new IllegalStateException("No staged root XML for " + name + " at " + xml);
    }
    javax.xml.parsers.DocumentBuilderFactory factory =
        javax.xml.parsers.DocumentBuilderFactory.newInstance();
    factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
    org.w3c.dom.Document doc = factory.newDocumentBuilder().parse(xml);
    org.w3c.dom.NodeList layers = doc.getElementsByTagName(tag);
    if (layers.getLength() == 0) {
      return new LayerResult(new ColorDrawable(Color.TRANSPARENT));
    }
    org.w3c.dom.Element layer = (org.w3c.dom.Element) layers.item(0);
    String own = layer.getAttribute("android:drawable");
    if (own != null && !own.isEmpty()) {
      return new LayerResult(resolveLayer(context, own));
    }
    return resolveInsetChild(context, layer, 0);
  }

  /** Resolves an {@code <inset>}-wrapped layer, accumulating padding over
   *  up to 4 nesting hops (mirrors the C# stager's hop cap). Unknown
   *  wrappers resolve transparent, as before. */
  private static LayerResult resolveInsetChild(Context context, org.w3c.dom.Element parent, int hop)
      throws Exception {
    org.w3c.dom.NodeList kids = parent.getChildNodes();
    for (int i = 0; i < kids.getLength(); i++) {
      if (!(kids.item(i) instanceof org.w3c.dom.Element)) {
        continue;
      }
      org.w3c.dom.Element kid = (org.w3c.dom.Element) kids.item(i);
      if (!"inset".equals(kid.getTagName())) {
        continue;
      }
      float uniform = parseInsetFraction(kid.getAttribute("android:inset"));
      float left = parseInsetFraction(kid.getAttribute("android:insetLeft"));
      float top = parseInsetFraction(kid.getAttribute("android:insetTop"));
      float right = parseInsetFraction(kid.getAttribute("android:insetRight"));
      float bottom = parseInsetFraction(kid.getAttribute("android:insetBottom"));
      if (left == 0) {
        left = uniform;
      }
      if (top == 0) {
        top = uniform;
      }
      if (right == 0) {
        right = uniform;
      }
      if (bottom == 0) {
        bottom = uniform;
      }
      String own = kid.getAttribute("android:drawable");
      LayerResult inner;
      if (own != null && !own.isEmpty()) {
        inner = new LayerResult(resolveLayer(context, own));
      } else if (hop < 4) {
        inner = resolveInsetChild(context, kid, hop + 1);
      } else {
        inner = new LayerResult(new ColorDrawable(Color.TRANSPARENT));
      }
      return new LayerResult(inner.drawable,
          left + inner.insetLeft, top + inner.insetTop,
          right + inner.insetRight, bottom + inner.insetBottom);
    }
    return new LayerResult(new ColorDrawable(Color.TRANSPARENT));
  }

  /** Parses an inset value to a fraction of the output square. Dimensions
   *  are in 108dp-viewport units (the adaptive-icon viewport is always
   *  108dp); percentages are literal fractions. Empty or unparseable
   *  values are 0 (a side-specific 0 falls back to the uniform inset). */
  static float parseInsetFraction(String v) {
    if (v == null) {
      return 0;
    }
    v = v.trim();
    if (v.isEmpty()) {
      return 0;
    }
    try {
      if (v.endsWith("%")) {
        return Float.parseFloat(v.substring(0, v.length() - 1)) / 100f;
      }
      String num = v.replaceAll("[^0-9.]", "");
      if (num.isEmpty()) {
        return 0;
      }
      return Float.parseFloat(num) / 108f;
    } catch (NumberFormatException e) {
      return 0;
    }
  }

  private static Drawable resolveLayer(Context context, String ref) {
    if (ref == null || ref.isEmpty()) {
      return new ColorDrawable(Color.TRANSPARENT);
    }
    if (ref.startsWith("#")) {
      return new ColorDrawable(Color.parseColor(ref));
    }
    if (ref.startsWith("@drawable/")) {
      int nested = context.getResources().getIdentifier(
          ref.substring("@drawable/".length()), "drawable", context.getPackageName());
      if (nested != 0) {
        return context.getDrawable(nested);
      }
    }
    return new ColorDrawable(Color.TRANSPARENT);
  }

  /** Draws one drawable stretched exactly over a sizePx square. The canvas
   *  is pre-scaled by the intrinsic dimensions because LayoutLib drawables
   *  may ignore setBounds and paint at intrinsic size; for drawables that
   *  do honor bounds the math is identical to setting the full bounds. */
  static void drawFullBleed(Canvas canvas, Drawable d, int sizePx) {
    drawIntoRect(canvas, d, 0, 0, sizePx, sizePx);
  }

  /** Draws one drawable stretched over an arbitrary rect, with the same
   *  intrinsic-size scaling as {@link #drawFullBleed}. Used for
   *  {@code <inset>} layers, whose padding launchers honor. */
  static void drawIntoRect(Canvas canvas, Drawable d, float left, float top, float width, float height) {
    int iw = 0;
    int ih = 0;
    try {
      iw = d.getIntrinsicWidth();
      ih = d.getIntrinsicHeight();
    } catch (Exception e) {
      // Fall through to plain bounds below.
    }
    if (iw <= 0 || ih <= 0) {
      iw = Math.round(width);
      ih = Math.round(height);
    }
    canvas.save();
    canvas.translate(left, top);
    canvas.scale(width / (float) iw, height / (float) ih);
    d.setBounds(0, 0, iw, ih);
    d.draw(canvas);
    canvas.restore();
  }

  /** Full-bleed over-composite of two drawables at a fixed square size,
   *  with the foreground drawn into its inset rect when the staged layer
   *  carried an {@code <inset>} wrapper. */
  static final class LayersView extends View {
    private final Drawable background;
    private final LayerResult foreground;
    private final int sizePx;

    LayersView(Context context, Drawable background, LayerResult foreground, int sizePx) {
      super(context);
      this.background = background;
      this.foreground = foreground;
      this.sizePx = sizePx;
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
      setMeasuredDimension(sizePx, sizePx);
    }

    @Override
    protected void onDraw(Canvas canvas) {
      drawFullBleed(canvas, background, sizePx);
      float l = foreground.insetLeft * sizePx;
      float t = foreground.insetTop * sizePx;
      drawIntoRect(canvas, foreground.drawable, l, t,
          sizePx - l - foreground.insetRight * sizePx,
          sizePx - t - foreground.insetBottom * sizePx);
    }
  }

  /** One drawable drawn full-bleed at a fixed square size. */
  static final class FixedDrawView extends View {
    private final Drawable drawable;
    private final int sizePx;

    FixedDrawView(Context context, Drawable drawable, int sizePx) {
      super(context);
      this.drawable = drawable;
      this.sizePx = sizePx;
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
      setMeasuredDimension(sizePx, sizePx);
    }

    @Override
    protected void onDraw(Canvas canvas) {
      drawFullBleed(canvas, drawable, sizePx);
    }
  }
}
