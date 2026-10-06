using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Gdi = System.Drawing;

namespace Mana.Renderer;

// GPU drawing and typography have no dependency on a rules engine or table UI.
public sealed class Canvas : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly SpriteBatch batch;
    private readonly Texture2D pixel, disk, halo;
    private readonly BasicEffect surfaceEffect;
    private float scale = 1;
    private readonly Dictionary<TextKey, (Texture2D Texture, long Used)> text = [];
    private long frame;
    private readonly record struct TextKey(string Value, int Size, int Width, int Lines, bool Bold, bool Serif);
    public Canvas(GraphicsDevice device)
    {
        this.device = device; batch = new(device);
        pixel = new(device, 1, 1); pixel.SetData(new[] { Color.White });
        surfaceEffect = new(device) { TextureEnabled = true, VertexColorEnabled = true };
        disk = Radial(128, false); halo = Radial(128, true);
    }
    private Texture2D Radial(int size, bool glow)
    {
        var values = new Color[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) {
            float radius = Vector2.Distance(new(x + .5f, y + .5f), new(size / 2f)) / (size / 2f);
            float alpha = glow ? MathF.Pow(Math.Max(0, 1 - radius), 2.1f) : Math.Clamp((1 - radius) * size / 2, 0, 1);
            values[y * size + x] = Color.White * alpha;
        }
        var result = new Texture2D(device, size, size); result.SetData(values); return result;
    }
    public void Begin(float scale = 1)
    {
        frame++; this.scale = scale;
        Resume();
    }
    private void Resume()
    {
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, null, null, null, Matrix.CreateScale(scale));
    }
    public void End()
    {
        batch.End();
        if (text.Count > 600) foreach (var key in text.OrderBy(p => p.Value.Used).Take(text.Count - 450).Select(p => p.Key).ToArray()) { text[key].Texture.Dispose(); text.Remove(key); }
    }
    public void Fill(Rectangle bounds, Color color) { if (bounds.Width > 0 && bounds.Height > 0) batch.Draw(pixel, bounds, color); }
    public void Rounded(Rectangle r, Color color, int radius = 10)
    {
        radius = Math.Clamp(radius, 0, Math.Min(r.Width, r.Height) / 2);
        if (radius == 0) { Fill(r, color); return; }
        Fill(new(r.X + radius, r.Y, r.Width - radius * 2, r.Height), color);
        Fill(new(r.X, r.Y + radius, radius, r.Height - radius * 2), color);
        Fill(new(r.Right - radius, r.Y + radius, radius, r.Height - radius * 2), color);
        int half = disk.Width / 2;
        batch.Draw(disk, new Rectangle(r.X, r.Y, radius, radius), new Rectangle(0, 0, half, half), color);
        batch.Draw(disk, new Rectangle(r.Right - radius, r.Y, radius, radius), new Rectangle(half, 0, half, half), color);
        batch.Draw(disk, new Rectangle(r.X, r.Bottom - radius, radius, radius), new Rectangle(0, half, half, half), color);
        batch.Draw(disk, new Rectangle(r.Right - radius, r.Bottom - radius, radius, radius), new Rectangle(half, half, half, half), color);
    }
    public void Border(Rectangle r, Color color, int width = 1)
    {
        Fill(new(r.X, r.Y, r.Width, width), color); Fill(new(r.X, r.Bottom - width, r.Width, width), color);
        Fill(new(r.X, r.Y, width, r.Height), color); Fill(new(r.Right - width, r.Y, width, r.Height), color);
    }
    public void Panel(Rectangle r, Color fill, Color trim, int radius = 10)
    {
        Rounded(new(r.X + 2, r.Y + 5, r.Width, r.Height), Color.Black * .45f, radius);
        Rounded(r, trim, radius); r.Inflate(-1, -1); Rounded(r, fill, Math.Max(0, radius - 1));
    }
    public void Gradient(Rectangle r, Color top, Color bottom)
    {
        int count = Math.Min(64, r.Height);
        for (int i = 0; i < count; i++) {
            int y = r.Y + i * r.Height / count, next = r.Y + (i + 1) * r.Height / count;
            Fill(new(r.X, y, r.Width, next - y), Color.Lerp(top, bottom, i / (float)Math.Max(1, count - 1)));
        }
    }
    public void Circle(Vector2 center, float radius, Color color) => batch.Draw(disk, new Rectangle((int)(center.X - radius), (int)(center.Y - radius), (int)(2 * radius), (int)(2 * radius)), color);
    public void Glow(Vector2 center, Vector2 size, Color color) => batch.Draw(halo, new Rectangle((int)(center.X - size.X / 2), (int)(center.Y - size.Y / 2), (int)size.X, (int)size.Y), color);
    public void Ring(Vector2 center, float radius, Color color, float width = 2, float start = 0, float sweep = MathF.PI * 2)
    {
        int segments = Math.Max(4, (int)(Math.Abs(sweep) * radius / 6));
        var previous = center + new Vector2(MathF.Cos(start), MathF.Sin(start)) * radius;
        for (int i = 1; i <= segments; i++) { float angle = start + sweep * i / segments; var next = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius; Line(previous, next, color, width); previous = next; }
    }
    public void Line(Vector2 from, Vector2 to, Color color, float width = 2)
    {
        var delta = to - from;
        batch.Draw(pixel, from, null, color, MathF.Atan2(delta.Y, delta.X), new Vector2(0, .5f), new Vector2(delta.Length(), width), SpriteEffects.None, 0);
    }
    public void Arrow(Vector2 from, Vector2 to, Color color, float width = 10)
    {
        var delta = to - from;
        float length = delta.Length();
        if (!float.IsFinite(length) || length < 6 || width <= 0 || color.A == 0) return;
        var unit = delta / length; var side = new Vector2(-unit.Y, unit.X);
        // A direct, filled silhouette keeps the target unambiguous in every
        // direction. Scale the whole head down for neighboring combat cards.
        width *= Math.Min(1, length / (width * 5.6f));
        float headLength = width * 2.8f, headRadius = width * 1.65f;
        float opacity = color.A / 255f;
        var light = Color.Lerp(color, Color.White * opacity, .22f);
        var vertices = new VertexPositionColorTexture[27]; int at = 0;
        void Triangle(Vector2 a, Vector2 b, Vector2 c, Color aColor, Color bColor, Color cColor) {
            vertices[at++] = new(new(a, 0), aColor, Vector2.Zero);
            vertices[at++] = new(new(b, 0), bColor, Vector2.Zero);
            vertices[at++] = new(new(c, 0), cColor, Vector2.Zero);
        }
        void Layer(float border, Vector2 offset, Color shade, Color highlight) {
            var tail = from - unit * border + offset;
            var neck = to - unit * (headLength + border * .5f) + offset;
            var tip = to + unit * (border * 1.4f) + offset;
            var a = tail + side * (width * .24f + border);
            var b = neck + side * (width * .5f + border);
            var c = neck - side * (width * .5f + border);
            var d = tail - side * (width * .24f + border);
            Triangle(a, b, c, shade, shade, highlight);
            Triangle(a, c, d, shade, highlight, highlight);
            Triangle(neck + side * (headRadius + border), tip,
                neck - side * (headRadius + border), shade, highlight, highlight);
        }
        float rim = Math.Min(2, width * .2f);
        Layer(rim, new(2, 3), Color.Black * (.3f * opacity), Color.Black * (.3f * opacity));
        Layer(rim, Vector2.Zero, Color.Black * (.8f * opacity), Color.Black * (.8f * opacity));
        Layer(0, Vector2.Zero, color, light);
        Triangles(vertices, pixel);
    }
    public void Image(Texture2D texture, Rectangle bounds, float alpha = 1) => batch.Draw(texture, bounds, Color.White * alpha);
    public void Sprite(Texture2D texture, Vector2 center, Vector2 size, float rotation = 0, Color? tint = null, Rectangle? crop = null)
    {
        var source = crop ?? texture.Bounds;
        batch.Draw(texture, center, source, tint ?? Color.White, rotation, new Vector2(source.Width / 2f, source.Height / 2f), size / new Vector2(source.Width, source.Height), SpriteEffects.None, 0);
    }
    public void Quad(Vector2 center, Vector2 size, float angle, Color tint) => batch.Draw(pixel, center, null, tint, angle, new Vector2(.5f), size, SpriteEffects.None, 0);
    public void Surface(Texture2D? texture, Pose pose, Color tint)
    {
        if (!pose.IsProjected) { Sprite(texture ?? pixel, pose.Center, pose.Size, pose.Angle, tint); return; }
        // Small subdivisions preserve perspective UVs without a diagonal seam.
        const int steps = 4;
        var vertices = new VertexPositionColorTexture[steps * steps * 6]; int at = 0;
        VertexPositionColorTexture Vertex(float u, float v) => new(new Vector3(pose.Point(u, v), 0), tint, new(u, v));
        for (int y = 0; y < steps; y++) for (int x = 0; x < steps; x++) {
            float u = x / (float)steps, v = y / (float)steps, nextU = (x + 1f) / steps, nextV = (y + 1f) / steps;
            vertices[at++] = Vertex(u, v); vertices[at++] = Vertex(nextU, v); vertices[at++] = Vertex(nextU, nextV);
            vertices[at++] = Vertex(u, v); vertices[at++] = Vertex(nextU, nextV); vertices[at++] = Vertex(u, nextV);
        }
        Triangles(vertices, texture ?? pixel);
    }
    public void Polygon(IReadOnlyList<Vector2> points, Color color)
    {
        if (points.Count < 3) return;
        var vertices = new VertexPositionColorTexture[(points.Count - 2) * 3]; int at = 0;
        for (int i = 1; i < points.Count - 1; i++) {
            vertices[at++] = new(new(points[0], 0), color, Vector2.Zero);
            vertices[at++] = new(new(points[i], 0), color, Vector2.Zero);
            vertices[at++] = new(new(points[i + 1], 0), color, Vector2.Zero);
        }
        Triangles(vertices, pixel);
    }
    public void Portrait(Texture2D texture, Vector2 center, float radius, Color tint)
    {
        // Crop the illustration of an existing card into a seat medallion.
        // This is a circular mesh, so no square artwork leaks outside the rim.
        const int segments = 64;
        var vertices = new VertexPositionColorTexture[segments * 3];
        Vector2 uvCenter = new(.5f, .33f), uvRadius = new(.34f, .24f);
        for (int i = 0; i < segments; i++) {
            float a = i * MathF.Tau / segments, b = (i + 1) * MathF.Tau / segments;
            var from = new Vector2(MathF.Cos(a), MathF.Sin(a)); var to = new Vector2(MathF.Cos(b), MathF.Sin(b));
            vertices[i * 3] = new(new(center, 0), tint, uvCenter);
            vertices[i * 3 + 1] = new(new(center + from * radius, 0), tint, uvCenter + from * uvRadius);
            vertices[i * 3 + 2] = new(new(center + to * radius, 0), tint, uvCenter + to * uvRadius);
        }
        Triangles(vertices, texture);
    }
    public void ProjectedEllipse(Texture2D texture, Pose pose, Color tint)
    {
        const int rings = 12, segments = 96;
        var vertices = new VertexPositionColorTexture[rings * segments * 6]; int at = 0;
        VertexPositionColorTexture Vertex(float r, float angle) {
            var uv = new Vector2(.5f) + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (r / 2);
            return new(new(pose.Point(uv.X, uv.Y), 0), tint, uv);
        }
        for (int ring = 0; ring < rings; ring++) for (int segment = 0; segment < segments; segment++) {
            float inner = ring / (float)rings, outer = (ring + 1f) / rings;
            float a = segment * MathF.Tau / segments, b = (segment + 1) * MathF.Tau / segments;
            vertices[at++] = Vertex(inner, a); vertices[at++] = Vertex(outer, a); vertices[at++] = Vertex(outer, b);
            vertices[at++] = Vertex(inner, a); vertices[at++] = Vertex(outer, b); vertices[at++] = Vertex(inner, b);
        }
        Triangles(vertices, texture);
    }
    private void Triangles(VertexPositionColorTexture[] vertices, Texture2D texture)
    {
        batch.End();
        surfaceEffect.World = Matrix.CreateScale(scale);
        surfaceEffect.Projection = Matrix.CreateOrthographicOffCenter(0, device.Viewport.Width, device.Viewport.Height, 0, -1, 1);
        surfaceEffect.Texture = texture;
        device.BlendState = BlendState.AlphaBlend; device.DepthStencilState = DepthStencilState.None;
        device.RasterizerState = RasterizerState.CullNone; device.SamplerStates[0] = SamplerState.AnisotropicClamp;
        foreach (var pass in surfaceEffect.CurrentTechnique.Passes) { pass.Apply(); device.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, vertices.Length / 3); }
        Resume();
    }
    public void CardFrame(Pose pose, Color color, int width = 2)
    {
        var p = pose.Corners;
        for (int i = 0; i < p.Length; i++) Line(p[i], p[(i + 1) % p.Length], color, width);
    }
    public void CardFocus(Pose pose, Color accent)
    {
        var rim = pose with { Size = pose.Size + new Vector2(14) };
        // A dark separator keeps the rim legible over both light art and felt.
        // Broad, faint strokes soften the glow without washing out the face.
        CardFrame(rim, Color.Black * .9f, 9);
        CardFrame(rim, accent * .12f, 24);
        CardFrame(rim, accent * .24f, 14);
        CardFrame(rim, accent, 4);
        var corners = rim.Corners;
        for (int i = 0; i < corners.Length; i++) {
            var corner = corners[i];
            foreach (int neighbor in new[] { (i + 1) % 4, (i + 3) % 4 }) {
                var edge = corners[neighbor] - corner;
                float length = edge.Length();
                if (length > 0) Line(corner, corner + edge / length * Math.Min(24, length * .22f), new Color(255, 244, 211), 3);
            }
        }
    }
    public void CardAction(Pose pose, Color accent, float glow = 1)
    {
        // Keep a solid, wide edge even at the low point of the glow. The dark
        // separator and pale inner line distinguish it from both art and felt.
        var rim = pose with { Size = pose.Size + new Vector2(12) };
        CardFrame(rim, accent * (.12f * glow), 28);
        CardFrame(rim, accent * (.23f * glow), 19);
        CardFrame(rim, Color.Black * .9f, 11);
        CardFrame(rim, accent, 7);
        CardFrame(pose with { Size = pose.Size + new Vector2(3) }, Color.Lerp(accent, Color.White, .75f), 2);
    }
    public void CardFrame(Vector2 center, Vector2 size, float angle, Color color, int width = 2)
    {
        var horizontal = new Vector2(MathF.Cos(angle), MathF.Sin(angle)); var vertical = new Vector2(-horizontal.Y, horizontal.X);
        var a = center - horizontal * size.X / 2 - vertical * size.Y / 2; var b = a + horizontal * size.X;
        var c = b + vertical * size.Y; var d = a + vertical * size.Y;
        Line(a, b, color, width); Line(b, c, color, width); Line(c, d, color, width); Line(d, a, color, width);
    }
    public void Text(string? value, int x, int y, Color color, float size = 1, int width = 2000, int maxLines = 100, bool bold = false, bool serif = false)
    {
        if (string.IsNullOrEmpty(value) || width <= 0 || maxLines <= 0) return;
        var key = new TextKey(value.Replace("\r", ""), Math.Max(9, (int)Math.Round(22 * size)), Math.Min(2000, width), Math.Min(80, maxLines), bold, serif);
        if (!text.TryGetValue(key, out var cached)) cached = (RenderText(key), frame);
        text[key] = (cached.Texture, frame);
        batch.Draw(cached.Texture, new Vector2(x, y), color);
    }
    public void CenterText(string? value, Rectangle r, Color color, float size = 1, bool bold = false, bool serif = false)
    {
        if (string.IsNullOrEmpty(value)) return;
        var key = new TextKey(value, Math.Max(9, (int)Math.Round(22 * size)), r.Width, 1, bold, serif);
        if (!text.TryGetValue(key, out var cached)) cached = (RenderText(key), frame);
        text[key] = (cached.Texture, frame);
        batch.Draw(cached.Texture, new Vector2(r.X + Math.Max(0, (r.Width - cached.Texture.Width) / 2), r.Y + Math.Max(0, (r.Height - cached.Texture.Height) / 2)), color);
    }
    private Texture2D RenderText(TextKey key)
    {
        using var font = new Gdi.Font(key.Serif ? "Georgia" : "Segoe UI", key.Size, key.Bold ? Gdi.FontStyle.Bold : Gdi.FontStyle.Regular, Gdi.GraphicsUnit.Pixel);
        using var measure = new Gdi.Bitmap(1, 1); using var measuring = Gdi.Graphics.FromImage(measure);
        using var format = new Gdi.StringFormat(Gdi.StringFormat.GenericTypographic) { Trimming = Gdi.StringTrimming.EllipsisWord };
        var measured = measuring.MeasureString(key.Value, font, key.Width, format);
        int height = Math.Max(1, Math.Min((int)Math.Ceiling(measured.Height) + 4, (int)Math.Ceiling(font.GetHeight(measuring) * key.Lines) + 2));
        int width = Math.Max(1, Math.Min(key.Width, (int)Math.Ceiling(measured.Width) + 6));
        using var bitmap = new Gdi.Bitmap(width, height);
        using (var graphics = Gdi.Graphics.FromImage(bitmap)) {
            graphics.Clear(Gdi.Color.Transparent); graphics.TextRenderingHint = Gdi.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(key.Value, font, Gdi.Brushes.White, new Gdi.RectangleF(0, 0, width, height), format);
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, Gdi.Imaging.ImageFormat.Png); stream.Position = 0;
        var texture = Texture2D.FromStream(device, stream);
        var data = new Color[width * height]; texture.GetData(data);
        for (int i = 0; i < data.Length; i++) data[i] = Color.FromNonPremultiplied(data[i].R, data[i].G, data[i].B, data[i].A);
        texture.SetData(data); return texture;
    }
    public void Dispose() { foreach (var value in text.Values) value.Texture.Dispose(); surfaceEffect.Dispose(); batch.Dispose(); pixel.Dispose(); disk.Dispose(); halo.Dispose(); }
}
