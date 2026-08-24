// 对比 GDI (TextRenderer) 与 GDI+ (DrawString/MeasureString) 对 CJK 换行布局的速度
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

static class Bench6
{
    [STAThread]
    static int Main()
    {
        Font f = new Font("Times New Roman", 9.5f);
        string shortCjk = new string('中', 80);
        string shortLatin = new string('a', 80);
        StringFormat sf = new StringFormat(StringFormatFlags.NoClip);
        using (Bitmap bmp = new Bitmap(1, 1))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            // 1. GDI MeasureText WordBreak（现状）
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) TextRenderer.MeasureText(shortCjk, f, new Size(700, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            sw.Stop();
            Console.WriteLine("GDI MeasureText WordBreak CJK 80字: " + (sw.ElapsedMilliseconds / 50.0).ToString("F3") + " ms/次");
            sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) TextRenderer.MeasureText(shortLatin, f, new Size(700, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            sw.Stop();
            Console.WriteLine("GDI MeasureText WordBreak LATIN 80字: " + (sw.ElapsedMilliseconds / 50.0).ToString("F3") + " ms/次");
            // 2. GDI 单行测量（无 WordBreak）
            sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) TextRenderer.MeasureText(shortCjk, f);
            sw.Stop();
            Console.WriteLine("GDI MeasureText 单行 CJK 80字: " + (sw.ElapsedMilliseconds / 50.0).ToString("F3") + " ms/次");
            // 3. GDI+ MeasureString（带换行区域）
            sf.Trimming = StringTrimming.None;
            sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) g.MeasureString(shortCjk, f, new SizeF(700, 100000), sf);
            sw.Stop();
            Console.WriteLine("GDI+ MeasureString 换行 CJK 80字: " + (sw.ElapsedMilliseconds / 50.0).ToString("F3") + " ms/次");
            // 4. GDI+ MeasureString 长文本
            string longCjk = new string('中', 100000);
            sw = Stopwatch.StartNew();
            for (int i = 0; i < 3; i++) g.MeasureString(longCjk, f, new SizeF(700, 10000000), sf);
            sw.Stop();
            Console.WriteLine("GDI+ MeasureString 换行 CJK 100K字: " + (sw.ElapsedMilliseconds / 3.0).ToString("F3") + " ms/次");
        }
        return 0;
    }
}
