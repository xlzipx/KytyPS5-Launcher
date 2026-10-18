using System.Windows;
using System.Windows.Media;

namespace KytyLauncher
{
    /// <summary>An image icon for a sidebar item in place of its font glyph: used as a mask, so it takes the item's
    /// colour like the glyphs do.</summary>
    public static class NavIcon
    {
        public static readonly DependencyProperty ImageProperty =
            DependencyProperty.RegisterAttached("Image", typeof(ImageSource), typeof(NavIcon), new PropertyMetadata(null,
                (d, e) => d.SetValue(MaskProperty, e.NewValue is ImageSource source ? new ImageBrush(source) { Stretch = Stretch.Uniform } : null)));

        /// <summary>The image as a brush for the icon's OpacityMask (a binding inside a brush in a template does not
        /// resolve).</summary>
        public static readonly DependencyProperty MaskProperty =
            DependencyProperty.RegisterAttached("Mask", typeof(Brush), typeof(NavIcon), new PropertyMetadata(null));

        public static Brush GetMask(DependencyObject d) => (Brush)d.GetValue(MaskProperty);
        public static void SetMask(DependencyObject d, Brush value) => d.SetValue(MaskProperty, value);

        /// <summary>A dot beside a sidebar item: something new there (About: a launcher update).</summary>
        public static readonly DependencyProperty BadgeProperty =
            DependencyProperty.RegisterAttached("Badge", typeof(bool), typeof(NavIcon), new PropertyMetadata(false));

        public static bool GetBadge(DependencyObject d) => (bool)d.GetValue(BadgeProperty);
        public static void SetBadge(DependencyObject d, bool value) => d.SetValue(BadgeProperty, value);

        public static ImageSource GetImage(DependencyObject d) => (ImageSource)d.GetValue(ImageProperty);
        public static void SetImage(DependencyObject d, ImageSource value) => d.SetValue(ImageProperty, value);
    }
}
