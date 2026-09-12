using System;
using Microsoft.Xna.Framework;

namespace TerrariaModder.Core.UI
{
    /// <summary>Convert logical SpriteBatch bounds into a viewport-bounded device scissor.</summary>
    internal static class ClipGeometry
    {
        internal static Rectangle Calculate(int x, int y, int width, int height, Matrix transform, Rectangle viewport)
        {
            if (width <= 0 || height <= 0) return new Rectangle(viewport.X, viewport.Y, 0, 0);
            var a = Vector2.Transform(new Vector2(x, y), transform);
            var b = Vector2.Transform(new Vector2(x + width, y), transform);
            var c = Vector2.Transform(new Vector2(x, y + height), transform);
            var d = Vector2.Transform(new Vector2(x + width, y + height), transform);
            int left = Bound(Math.Floor(Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X))) + viewport.X, viewport.Left, viewport.Right);
            int top = Bound(Math.Floor(Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y))) + viewport.Y, viewport.Top, viewport.Bottom);
            int right = Bound(Math.Ceiling(Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X))) + viewport.X, viewport.Left, viewport.Right);
            int bottom = Bound(Math.Ceiling(Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y))) + viewport.Y, viewport.Top, viewport.Bottom);
            return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }
        private static int Bound(double value, int min, int max) => (int)Math.Max(min, Math.Min(max, value));
    }
}
