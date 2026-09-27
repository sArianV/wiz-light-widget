using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WizLightWidget.Controls;

/// <summary>
/// Dibuja una copia semitransparente del elemento que se está arrastrando,
/// siguiendo el cursor, para que el drag-and-drop se sienta "vivo" en vez de
/// mostrar solo el cursor del sistema sin ningún feedback visual.
/// </summary>
public class DragAdorner : Adorner
{
    private readonly Brush _brush;
    private readonly Size _size;
    private readonly Point _grabOffset;
    private Point _topLeft;

    /// <param name="grabOffset">
    /// Posición del cursor relativa a la esquina superior izquierda del elemento arrastrado,
    /// en el momento en que empezó el arrastre (p.ej. dónde está la manija dentro de la tarjeta),
    /// para que el "fantasma" no se recentre bajo el cursor y mantenga ese mismo punto de agarre.
    /// </param>
    public DragAdorner(UIElement adornedElement, UIElement dragged, Point grabOffset) : base(adornedElement)
    {
        IsHitTestVisible = false;
        _grabOffset = grabOffset;

        _size = new Size(Math.Max(1, dragged.RenderSize.Width), Math.Max(1, dragged.RenderSize.Height));

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(_size.Width)),
            Math.Max(1, (int)Math.Ceiling(_size.Height)),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dragged);

        _brush = new ImageBrush(bitmap) { Opacity = 0.85 };
    }

    public void UpdatePosition(Point cursorPosition)
    {
        _topLeft = new Point(cursorPosition.X - _grabOffset.X, cursorPosition.Y - _grabOffset.Y);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRoundedRectangle(_brush, null, new Rect(_topLeft, _size), 10, 10);
    }
}
