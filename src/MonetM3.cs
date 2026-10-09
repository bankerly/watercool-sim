// MonetM3.cs —— 谷歌官方 Material 3 取色链路（HCT / CAM16 / TonalPalette / SchemeTonalSpot）
// 依据：material-color-utilities（Google, Apache-2.0）的 Hct / Cam16 / TonalPalette / SchemeTonalSpot 与
//       Celebi 量化 + Score 选色思路，用 C# 重新实现（不引入任何第三方二进制）。
using System;
using System.Collections.Generic;
using System.Drawing;

namespace WaterCoolSim
{
    public static class CM
    {
        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
        public static double SanitizeDegrees(double d) { d = d % 360.0; if (d < 0) d += 360.0; return d; }
        public static double Deg(double rad) { return rad * 180.0 / Math.PI; }
        public static double Rad(double deg) { return deg * Math.PI / 180.0; }
        public static int Byte(double v) { int i = (int)Math.Round(v); return i < 0 ? 0 : (i > 255 ? 255 : i); }

        // L* ↔ Y（官方 Hct 用同一组常数）
        public static double LstarFromY(double y)
        {
            double e = 216.0 / 24389.0, kappa = 24389.0 / 27.0;
            double yf = y / 100.0;
            if (yf <= e) return yf * kappa;
            return 116.0 * Math.Pow(yf, 1.0 / 3.0) - 16.0;
        }
        public static double YFromLstar(double lstar)
        {
            double e = 216.0 / 24389.0, kappa = 24389.0 / 27.0;
            if (lstar > 8.0) return 100.0 * Math.Pow((lstar + 16.0) / 116.0, 3.0);
            return 100.0 * lstar / kappa;
        }

        // sRGB(0-255) → XYZ(D65, 0-100)
        public static double[] XyzFromRgb(int r, int g, int b)
        {
            double[] ch = { r / 255.0, g / 255.0, b / 255.0 };
            double[] lin = new double[3];
            for (int i = 0; i < 3; i++)
                lin[i] = ch[i] <= 0.040449936 ? ch[i] / 12.92 : Math.Pow((ch[i] + 0.055) / 1.055, 2.4);
            double x = lin[0] * 0.4124 + lin[1] * 0.3576 + lin[2] * 0.1805;
            double y = lin[0] * 0.2126 + lin[1] * 0.7152 + lin[2] * 0.0722;
            double z = lin[0] * 0.0193 + lin[1] * 0.1192 + lin[2] * 0.9505;
            return new double[] { x * 100.0, y * 100.0, z * 100.0 };
        }

        public static bool XyzToRgb(double[] xyz, out int r, out int g, out int b, bool checkGamut)
        {
            double[] lin = new double[3];
            lin[0] = 3.2406 * xyz[0] / 100.0 - 1.5372 * xyz[1] / 100.0 - 0.4986 * xyz[2] / 100.0;
            lin[1] = -0.9689 * xyz[0] / 100.0 + 1.8758 * xyz[1] / 100.0 + 0.0415 * xyz[2] / 100.0;
            lin[2] = 0.0557 * xyz[0] / 100.0 - 0.2040 * xyz[1] / 100.0 + 1.0570 * xyz[2] / 100.0;
            if (checkGamut)
            {
                for (int i = 0; i < 3; i++) if (lin[i] < -0.0005 || lin[i] > 1.0005) { r = g = b = 0; return false; }
            }
            double[] ch = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double v = CM.Clamp(lin[i], 0.0, 1.0);
                ch[i] = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
            }
            r = CM.Byte(ch[0] * 255.0); g = CM.Byte(ch[1] * 255.0); b = CM.Byte(ch[2] * 255.0);
            return true;
        }
    }

    // ============================================================
    //  CAM16（官方 ViewingConditions 默认参数）
    // ============================================================
    public class Cam16Vc
    {
        public double[] rgbD = new double[3];
        public double[] whitePoint = new double[3];
        public double n, z, nbb, ncb, c, nc, aw, fl, flRoot;

        private static Cam16Vc def;

        public static Cam16Vc Default
        {
            get
            {
                if (def == null)
                    def = Make(new double[] { 95.047, 100.0, 108.883 },
                               (200.0 / Math.PI) * CM.YFromLstar(50.0) / 100.0, 50.0, 2.0, false);
                return def;
            }
        }

        public static Cam16Vc Make(double[] wp, double adaptingLuminance, double backgroundLstar, double surround, bool discounting)
        {
            Cam16Vc vc = new Cam16Vc();
            vc.whitePoint = wp;
            double xw = wp[0], yw = wp[1], zw = wp[2];
            double rW = 0.401288 * xw + 0.650173 * yw - 0.051461 * zw;
            double gW = -0.250268 * xw + 1.204414 * yw + 0.045854 * zw;
            double bW = -0.002079 * xw + 0.048952 * yw + 0.953127 * zw;
            double f = 0.8 + surround / 10.0;
            double cc = f >= 0.9 ? CM.Lerp(0.59, 0.69, (f - 0.9) * 10.0) : CM.Lerp(0.525, 0.59, (f - 0.8) * 10.0);
            double d = discounting ? 1.0 : CM.Clamp(1.0 - (1.0 / 3.6) * Math.Exp((-adaptingLuminance - 42.0) / 92.0), 0.0, 1.0);
            vc.nc = f;
            vc.c = cc;
            double[] rgbW = { rW, gW, bW };
            for (int i = 0; i < 3; i++) vc.rgbD[i] = d * (100.0 / rgbW[i]) + 1.0 - d;
            double k = 1.0 / (5.0 * adaptingLuminance + 1.0);
            double k4 = k * k * k * k;
            double k4F = 1.0 - k4;
            vc.fl = k4 * adaptingLuminance + k4F * k4F * 0.1 * Math.Pow(5.0 * adaptingLuminance, 1.0 / 3.0);
            vc.flRoot = Math.Pow(vc.fl, 0.25);
            double n = CM.YFromLstar(backgroundLstar) / wp[1];
            vc.n = n;
            vc.z = 1.48 + Math.Sqrt(n);
            vc.nbb = 0.725 * Math.Pow(n, -0.2);
            vc.ncb = vc.nbb;
            double[] rgbA = { PostAdapt(vc.rgbD[0] * rW, vc), PostAdapt(vc.rgbD[1] * gW, vc), PostAdapt(vc.rgbD[2] * bW, vc) };
            vc.aw = (2.0 * rgbA[0] + rgbA[1] + rgbA[2] / 20.0 - 0.305) * vc.nbb;
            return vc;
        }

        // 与 material-color-utilities 一致：这一版简化 CAM16 不把 FL 计入适应（rgbAFactors = pow(k4*rgbD + k4F, 0.42)）
        public static double PostAdapt(double cc, Cam16Vc vc)
        {
            double abs = Math.Abs(cc) / 100.0;
            double p = Math.Pow(abs, 0.42);
            double v = 400.0 * p / (p + 27.13);
            return (cc < 0 ? -v : v) + 0.1;
        }

        public static double UnPostAdapt(double ca, Cam16Vc vc)
        {
            double t = Math.Abs(ca - 0.1);
            if (t < 1e-12) return 0.0;
            if (t > 400.0) t = 400.0;
            double v = 100.0 * Math.Pow(27.13 * t / (400.0 - t), 1.0 / 0.42);
            return ca < 0.1 ? -v : v;
        }
    }

    public static class Cam16
    {
        public static double[] Forward(double x, double y, double z, Cam16Vc vc)
        {
            double rC = 0.401288 * x + 0.650173 * y - 0.051461 * z;
            double gC = -0.250268 * x + 1.204414 * y + 0.045854 * z;
            double bC = -0.002079 * x + 0.048952 * y + 0.953127 * z;
            double rA = Cam16Vc.PostAdapt(vc.rgbD[0] * rC, vc);
            double gA = Cam16Vc.PostAdapt(vc.rgbD[1] * gC, vc);
            double bA = Cam16Vc.PostAdapt(vc.rgbD[2] * bC, vc);
            double a = rA - 12.0 * gA / 11.0 + bA / 11.0;
            double bb = (rA + gA - 2.0 * bA) / 9.0;
            double hRad = Math.Atan2(bb, a);
            double h = CM.SanitizeDegrees(CM.Deg(hRad));
            double et = 0.25 * (Math.Cos(hRad + 2.0) + 3.8);
            double A = (2.0 * rA + gA + bA / 20.0 - 0.305) * vc.nbb;
            double J = 100.0 * Math.Pow(Math.Max(A, 0.0) / vc.aw, vc.c * vc.z);
            double denom = rA + gA + 21.0 * bA / 20.0;
            double t = denom <= 1e-9 ? 0.0
                : (50000.0 / 13.0) * vc.nc * vc.ncb * et * Math.Sqrt(a * a + bb * bb) / denom;
            double alpha = Math.Pow(Math.Max(t, 0.0), 0.9) * Math.Pow(1.64 - Math.Pow(0.29, vc.n), 0.73);
            double C = alpha * Math.Sqrt(J / 100.0);
            return new double[] { J, C, h };
        }

        public static double[] Inverse(double J, double C, double hDeg, Cam16Vc vc)
        {
            if (J <= 0.0) return new double[] { 0.0, 0.0, 0.0 };
            double alpha = C <= 0.0 ? 0.0 : C / Math.Sqrt(J / 100.0);
            double t = alpha <= 0.0 ? 0.0
                : Math.Pow(alpha / Math.Pow(1.64 - Math.Pow(0.29, vc.n), 0.73), 1.0 / 0.9);
            double hRad = CM.Rad(hDeg);
            double et = 0.25 * (Math.Cos(hRad + 2.0) + 3.8);
            double A = vc.aw * Math.Pow(J / 100.0, 1.0 / (vc.c * vc.z));
            double p1 = t <= 1e-8 ? 0.0 : (50000.0 / 13.0) * vc.nc * vc.ncb * et / t;
            double p2 = A / vc.nbb + 0.305;
            double p3 = 21.0 / 20.0;
            double sinH = Math.Sin(hRad), cosH = Math.Cos(hRad);
            double a, bb;
            if (t <= 1e-8) { a = 0.0; bb = 0.0; }
            else if (Math.Abs(sinH) >= Math.Abs(cosH))
            {
                double p4 = p1 / sinH;
                bb = (p2 * (2.0 + p3) * (460.0 / 1403.0))
                   / (p4 + (2.0 + p3) * (220.0 / 1403.0) * (cosH / sinH) - (27.0 / 1403.0) + p3 * (6300.0 / 1403.0));
                a = bb * (cosH / sinH);
            }
            else
            {
                double p5 = p1 / cosH;
                a = (p2 * (2.0 + p3) * (460.0 / 1403.0))
                  / (p5 + (2.0 + p3) * (220.0 / 1403.0) - ((27.0 / 1403.0) - p3 * (6300.0 / 1403.0)) * (sinH / cosH));
                bb = a * (sinH / cosH);
            }
            double rA = (460.0 * p2 + 451.0 * a + 288.0 * bb) / 1403.0;
            double gA = (460.0 * p2 - 891.0 * a - 261.0 * bb) / 1403.0;
            double bA = (460.0 * p2 - 220.0 * a - 6300.0 * bb) / 1403.0;
            double rC = Cam16Vc.UnPostAdapt(rA, vc) / vc.rgbD[0];
            double gC = Cam16Vc.UnPostAdapt(gA, vc) / vc.rgbD[1];
            double bC = Cam16Vc.UnPostAdapt(bA, vc) / vc.rgbD[2];
            double x = 1.8620678550872327 * rC - 1.0112546305316843 * gC + 0.14918677544445176 * bC;
            double y = 0.3875265432361372 * rC + 0.6214474419314753 * gC - 0.008973985167612518 * bC;
            double z = -0.015841498849333856 * rC - 0.03412293802851557 * gC + 1.0499644368778496 * bC;
            return new double[] { x, y, z };
        }
    }

    // ============================================================
    //  HCT：Hue / Chroma(CAM16) / Tone(L*)
    // ============================================================
    public static class Hct
    {
        public static double[] FromRgb(int r, int g, int b)
        {
            double[] xyz = CM.XyzFromRgb(r, g, b);
            double[] cam = Cam16.Forward(xyz[0], xyz[1], xyz[2], Cam16Vc.Default);
            return new double[] { cam[2], cam[1], CM.LstarFromY(xyz[1]) };   // hue, chroma, tone
        }

        private static bool SolveAtChroma(double hue, double chroma, double yTarget, out int r, out int g, out int b)
        {
            double lo = 0.0, hi = 100.0;
            for (int i = 0; i < 36; i++)
            {
                double mid = (lo + hi) / 2.0;
                double[] xyz = Cam16.Inverse(mid, chroma, hue, Cam16Vc.Default);
                if (xyz[1] < yTarget) lo = mid; else hi = mid;
            }
            double[] xyzF = Cam16.Inverse((lo + hi) / 2.0, chroma, hue, Cam16Vc.Default);
            return CM.XyzToRgb(xyzF, out r, out g, out b, true);
        }

        // 官方 Hct.from(hue, chroma, tone) 的等效实现：命中 L* 指定的 tone，超出色域就降色度
        public static int FromHct(double hue, double chroma, double tone)
        {
            if (tone <= 0.0) return unchecked((int)0xFF000000);
            if (tone >= 100.0) return unchecked((int)0xFFFFFFFF);
            double yTarget = CM.YFromLstar(tone);
            double c = Math.Max(0.0, chroma);
            int r, g, b;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (SolveAtChroma(hue, c, yTarget, out r, out g, out b))
                    return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                c *= 0.9;
                if (c < 0.5) break;
            }
            // 完全没色度：用中性灰
            int gray = CM.Byte(255.0 * Math.Pow(CM.YFromLstar(tone) / 100.0, 1.0 / 2.2));
            // 更准确：按 L* 走一遍灰阶
            double yv = CM.YFromLstar(tone) / 100.0;
            double lin = yv <= 0.0031308 ? yv * 12.92 : 1.055 * Math.Pow(yv, 1.0 / 2.4) - 0.055;
            gray = CM.Byte(lin * 255.0);
            return unchecked((int)0xFF000000) | (gray << 16) | (gray << 8) | gray;
        }
    }

    public class TonalPalette
    {
        public double Hue;
        public double Chroma;

        public TonalPalette(double hue, double chroma) { Hue = hue; Chroma = chroma; }

        public Color Tone(double t)
        {
            int argb = Hct.FromHct(Hue, Chroma, t);
            return Color.FromArgb(argb);
        }
    }

    // ============================================================
    //  主色选择：分桶量化（Celebi 思路）+ HCT 打分（含官方黄绿降权/邻域加权）
    // ============================================================
    public static class SourceColor
    {
        public static int Pick(Bitmap bmp)
        {
            int W = 112;
            int H = Math.Max(1, bmp.Height * W / Math.Max(1, bmp.Width));
            if (H > 112) H = 112;
            Dictionary<int, int[]> buckets = new Dictionary<int, int[]>();
            using (Bitmap small = new Bitmap(W, H))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(bmp, 0, 0, W, H);
                }
                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        int key = ((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3);
                        int[] acc;
                        if (!buckets.TryGetValue(key, out acc)) { acc = new int[5]; buckets[key] = acc; }
                        acc[0] += c.R; acc[1] += c.G; acc[2] += c.B; acc[3] += 1;
                    }
                }
            }
            // 桶均值 + 人口
            List<int[]> entries = new List<int[]>();        // {r,g,b,pop,hue,chroma,tone}
            double[] huePop = new double[360];
            double total = 0;
            foreach (KeyValuePair<int, int[]> kv in buckets)
            {
                int[] a = kv.Value;
                int cnt = a[3];
                if (cnt <= 0) continue;
                int r = a[0] / cnt, g = a[1] / cnt, b = a[2] / cnt;
                double[] hct = Hct.FromRgb(r, g, b);
                int hue = (int)Math.Floor(hct[0]) % 360;
                if (hue < 0) hue += 360;
                huePop[hue] += cnt;
                total += cnt;
                entries.Add(new int[] { r, g, b, cnt, hue, (int)Math.Round(hct[1]), (int)Math.Round(hct[2]) });
            }
            if (entries.Count == 0) return unchecked((int)0xFF6750A4);
            // 邻域占比（±15°）
            double[] excited = new double[360];
            for (int hue = 0; hue < 360; hue++)
            {
                double sum = 0;
                for (int i = hue - 15; i <= hue + 15; i++)
                {
                    int k = ((i % 360) + 360) % 360;
                    sum += huePop[k];
                }
                excited[hue] = total > 0 ? sum / total : 0;
            }
            int best = 0;
            double bestScore = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                int[] e = entries[i];
                double chroma = e[5], hue = e[4], tone = e[6];
                double score = e[3] * (0.45 + 1.55 * excited[e[4]]);
                if (chroma > 60 && hue > 82 && hue < 158) score *= 0.01;        // 官方：过于鲜艳的黄绿不要
                else if (chroma > 10 && hue > 80 && hue < 170) score *= 0.5;    // 黄/黄绿降权
                if (chroma < 5) score *= 0.08;                                  // 近灰不要
                if (tone < 12 || tone > 96) score *= 0.2;                       // 过黑/过白不要
                if (score > bestScore) { bestScore = score; best = i; }
            }
            int[] bc = entries[best];
            return unchecked((int)0xFF000000) | (bc[0] << 16) | (bc[1] << 8) | bc[2];
        }

        // 官方 SchemeTonalSpot：主色从 seed 的 HCT 派生，色度固定档位
        public static Palette SchemeFromSeed(Color seed, bool dark)
        {
            double[] hct = Hct.FromRgb(seed.R, seed.G, seed.B);
            double hue = hct[0], chroma = hct[1];
            double chromaClamp = Math.Max(chroma, 48.0);           // 官方：太灰的壁纸也保证主色有色彩
            TonalPalette primary = new TonalPalette(hue, chromaClamp);
            TonalPalette secondary = new TonalPalette(hue, 16.0);
            TonalPalette tertiary = new TonalPalette(CM.SanitizeDegrees(hue + 60.0), 24.0);
            TonalPalette neutral = new TonalPalette(hue, 6.0);
            TonalPalette neutralVariant = new TonalPalette(hue, 8.0);
            TonalPalette error = new TonalPalette(25.0, 84.0);

            Palette p = new Palette();
            p.Dark = dark;
            p.SourceColor = seed;
            if (!dark)
            {
                p.Primary = primary.Tone(40); p.OnPrimary = primary.Tone(100);
                p.PrimaryContainer = primary.Tone(90); p.OnPrimaryContainer = primary.Tone(10);
                p.Secondary = secondary.Tone(40); p.OnSecondary = secondary.Tone(100);
                p.SecondaryContainer = secondary.Tone(90); p.OnSecondaryContainer = secondary.Tone(10);
                p.Tertiary = tertiary.Tone(40); p.TertiaryContainer = tertiary.Tone(90);
                p.Error = error.Tone(40); p.OnError = error.Tone(100);
                p.ErrorContainer = error.Tone(90); p.OnErrorContainer = error.Tone(10);
                p.Surface = neutral.Tone(99); p.OnSurface = neutral.Tone(10);
                p.SurfaceVariant = neutralVariant.Tone(90); p.OnSurfaceVariant = neutralVariant.Tone(30);
                p.Outline = neutralVariant.Tone(50); p.OutlineVariant = neutralVariant.Tone(80);
                p.SurfaceLow = neutral.Tone(96); p.SurfaceContainer = neutral.Tone(94);
                p.SurfaceHigh = neutral.Tone(92); p.SurfaceHighest = neutral.Tone(90);
                p.InverseSurface = neutral.Tone(20); p.InverseOnSurface = neutral.Tone(95);
            }
            else
            {
                p.Primary = primary.Tone(80); p.OnPrimary = primary.Tone(20);
                p.PrimaryContainer = primary.Tone(30); p.OnPrimaryContainer = primary.Tone(90);
                p.Secondary = secondary.Tone(80); p.OnSecondary = secondary.Tone(20);
                p.SecondaryContainer = secondary.Tone(30); p.OnSecondaryContainer = secondary.Tone(90);
                p.Tertiary = tertiary.Tone(80); p.TertiaryContainer = tertiary.Tone(30);
                p.Error = error.Tone(80); p.OnError = error.Tone(20);
                p.ErrorContainer = error.Tone(30); p.OnErrorContainer = error.Tone(90);
                p.Surface = neutral.Tone(6); p.OnSurface = neutral.Tone(90);
                p.SurfaceVariant = neutralVariant.Tone(30); p.OnSurfaceVariant = neutralVariant.Tone(80);
                p.Outline = neutralVariant.Tone(60); p.OutlineVariant = neutralVariant.Tone(30);
                p.SurfaceLow = neutral.Tone(10); p.SurfaceContainer = neutral.Tone(12);
                p.SurfaceHigh = neutral.Tone(17); p.SurfaceHighest = neutral.Tone(22);
                p.InverseSurface = neutral.Tone(90); p.InverseOnSurface = neutral.Tone(20);
            }
            p.StateHover = dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
            p.StatePress = dark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(34, 0, 0, 0);
            return p;
        }
    }
}
