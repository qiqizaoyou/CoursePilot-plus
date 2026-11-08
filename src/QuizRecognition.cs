using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CoursePilot
{
    public sealed class QuizChoice
    {
        public string Label;
        public Rectangle Bounds;
        public bool Selected;
        public Point Center { get { return new Point(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2); } }
        internal static bool IsTruthLabel(string label) { return label == "对" || label == "错"; }
    }

    public sealed class QuizQuestion
    {
        public string Id, Type, Number;
        public int Top, Bottom;
        public List<QuizChoice> Options = new List<QuizChoice>();
        public List<QuizBlank> Blanks = new List<QuizBlank>();
        internal bool UsesTruthLabels { get { return Type == "judge" && Options.Count == 2 && Options.All(o => QuizChoice.IsTruthLabel(o.Label)); } }
    }

    internal sealed class QuizMarker
    {
        public string Id, Type, Number;
        public int Top, Height;
    }

    internal sealed class QuizPage : IDisposable
    {
        public Bitmap Image;
        public QuizTextPage Text;
        public List<QuizChoice> Options;
        public List<QuizBlank> Blanks = new List<QuizBlank>();
        public int Top;
        public int Bottom { get { return Top + Image.Height; } }
        internal int? FooterBoundary
        {
            get
            {
                QuizText footer = Text.Lines.LastOrDefault(line => line.Bounds.Top > Image.Height * .15 && QuizVision.Footer(line.Text));
                return footer == null ? (int?)null : Top + footer.Bounds.Top - 8;
            }
        }
        public void Dispose() { Image.Dispose(); }
    }

    internal sealed class QuizPixels
    {
        private readonly byte[] bytes;
        private readonly int stride;
        internal readonly int Width, Height;
        internal QuizPixels(Bitmap bitmap)
        {
            Width = bitmap.Width; Height = bitmap.Height;
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            stride = Width * 3; bytes = new byte[stride * Height];
            try { for (int y = 0; y < Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * stride, stride); }
            finally { bitmap.UnlockBits(data); }
        }
        internal Color At(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return Color.White;
            int i = y * stride + x * 3;
            return Color.FromArgb(bytes[i + 2], bytes[i + 1], bytes[i]);
        }
        internal bool Blue(int x, int y)
        {
            Color c = At(x, y);
            return c.B >= 175 && c.G >= 105 && c.B - c.R >= 9 && c.B - c.G >= 3;
        }
        internal bool Yellow(int x, int y)
        {
            Color c = At(x, y);
            return c.R >= 160 && c.G >= 100 && c.G <= c.R * .94 && c.B <= c.G * .82 && c.R - c.B >= 65;
        }
        internal bool Status(int x, int y, DotColor color)
        {
            if (color == DotColor.Yellow) return Yellow(x, y);
            Color c = At(x, y);
            return c.G >= 105 && c.G > c.R * 1.20 && c.G > c.B * 1.15;
        }
    }

    internal static class QuizVision
    {
        internal static Bitmap OcrInput(Bitmap source)
        {
            Bitmap copy = source.Clone(new Rectangle(Point.Empty, source.Size), PixelFormat.Format24bppRgb);
            QuizPixels pixels = new QuizPixels(source);
            int limit = Math.Min(source.Width, Math.Max(160, (int)(source.Width * .25) + 1));
            bool[] visited = new bool[limit * source.Height];
            int[] queue = new int[visited.Length];
            for (int y = 1; y < source.Height - 1; y++)
                for (int x = 1; x < limit - 1; x++)
                {
                    int index = y * limit + x;
                    Color color = pixels.At(x, y);
                    if (visited[index] || color.B < 170 || color.B - color.R < 60 || color.G > 225) continue;
                    queue[0] = index; visited[index] = true;
                    int head = 0, tail = 1, x1 = x, x2 = x, y1 = y, y2 = y;
                    while (head < tail)
                    {
                        int current = queue[head++], cx = current % limit, cy = current / limit;
                        x1 = Math.Min(x1, cx); x2 = Math.Max(x2, cx); y1 = Math.Min(y1, cy); y2 = Math.Max(y2, cy);
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = cx + dx, ny = cy + dy;
                                if (nx < 0 || ny < 0 || nx >= limit || ny >= source.Height) continue;
                                int next = ny * limit + nx;
                                if (visited[next]) continue;
                                Color c = pixels.At(nx, ny);
                                if (c.B >= 170 && c.B - c.R >= 60 && c.G <= 225) { visited[next] = true; queue[tail++] = next; }
                            }
                    }
                    int w = x2 - x1 + 1, h = y2 - y1 + 1;
                    double fill = tail / (double)(w * h);
                    if (w < 14 || h < 14 || w > 130 || h > 130 || w / (double)h < .82 || w / (double)h > 1.22 || fill < .42 || fill > .88) continue;
                    double centerX = (x1 + x2) / 2.0, centerY = (y1 + y2) / 2.0, radius = Math.Min(w, h) / 2.0;
                    for (int py = Math.Max(0, y1 - 2); py <= Math.Min(source.Height - 1, y2 + 2); py++)
                        for (int px = Math.Max(0, x1 - 2); px <= Math.Min(source.Width - 1, x2 + 2); px++)
                        {
                            double distance = (px - centerX) * (px - centerX) + (py - centerY) * (py - centerY);
                            Color c = pixels.At(px, py);
                            bool glyph = distance < radius * radius * .68 && c.R > 170 && c.G > 190 && c.B > 200;
                            copy.SetPixel(px, py, glyph ? Color.Black : Color.White);
                        }
                }
            return copy;
        }
        internal static string Normalize(string text) { return Regex.Replace(text ?? "", @"\s+", "").Replace("．", "."); }
        internal static string Anchor(string text)
        {
            string value = Normalize(text);
            return Regex.Replace(value, @"^[A-H](?=[\u4e00-\u9fff])", "");
        }
        private const string QuestionTypes = @"(单选题?|多选题?|判断题?|填空题?|简答题?|论述题?|计算题?)";
        internal static string UnnumberedType(string text)
        {
            Match match = Regex.Match(Normalize(text), @"^[【\[（(〔〖]" + QuestionTypes + @"[】\]）)〕〗]");
            return match.Success ? match.Groups[1].Value : null;
        }
        internal static QuizMarker Marker(QuizText line, int pageTop, int width)
        {
            if (line.Bounds.Left > width * .26) return null;
            string value = Normalize(line.Text);
            Match match = Regex.Match(value, @"^(\d{1,3})[.、]?[【\[（(〔〖]?" + QuestionTypes + @"[】\]）)〕〗]?");
            if (!match.Success) return null;
            string type = match.Groups[2].Value.StartsWith("单选") ? "single" : match.Groups[2].Value.StartsWith("多选") ? "multiple" : match.Groups[2].Value.StartsWith("判断") ? "judge" : match.Groups[2].Value.StartsWith("填空") ? "fill" : "unsupported";
            string key = type + ":" + match.Groups[1].Value + ":" + value;
            string hash;
            using (SHA256 digest = SHA256.Create()) hash = BitConverter.ToString(digest.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 12);
            return new QuizMarker { Id = type + "-" + match.Groups[1].Value + "-" + hash, Type = type, Number = match.Groups[1].Value, Top = pageTop + line.Bounds.Top, Height = line.Bounds.Height };
        }
        internal static bool Footer(string text)
        {
            string value = Normalize(text);
            // OCR can put the two adjacent buttons on a single line.
            const string save = "暂时保存";
            if (value.StartsWith(save, StringComparison.Ordinal)) value = value.Substring(save.Length);
            return value == "提交" || value == "提交答案" || value == "提交测验" || value == "提交作业" || value == "提交本次测验";
        }
        internal static int Total(QuizTextPage text)
        {
            string value = String.Join("", text.Lines.Select(l => Normalize(l.Text)));
            Match count = Regex.Match(value, @"题[量数][:：]?(\d{1,3})");
            if (!count.Success) count = Regex.Match(value, @"共(\d{1,3})题");
            return count.Success ? Int32.Parse(count.Groups[1].Value) : 0;
        }
        internal static int ExplicitTotal(QuizTextPage text)
        {
            // Resume can show only a section header. Its "共 N 题" is not the quiz total.
            foreach (QuizText line in text.Lines)
            {
                Match count = Regex.Match(Normalize(line.Text), @"^(?:总)?题[量数][:：]?(\d{1,3})(?!\d)");
                if (count.Success) return Int32.Parse(count.Groups[1].Value);
            }
            return 0;
        }
        internal static bool HasQuizTitle(QuizTextPage text)
        {
            return text.Lines.Any(l => Normalize(l.Text).Contains("章节测验") || Normalize(l.Text).Contains("章节测试"));
        }
        internal static int YellowCount(Bitmap image)
        { return StatusCount(image, DotColor.Yellow); }
        internal static int GreenCount(Bitmap image)
        { return StatusCount(image, DotColor.Green); }
        private static int StatusCount(Bitmap image, DotColor color)
        {
            QuizPixels pixels = new QuizPixels(image);
            bool[] visited = new bool[image.Width * image.Height];
            int[] queue = new int[visited.Length];
            int count = 0;
            for (int y = 1; y < image.Height - 1; y++)
                for (int x = 1; x < image.Width - 1; x++)
                {
                    int start = y * image.Width + x;
                    if (visited[start] || !pixels.Status(x, y, color)) continue;
                    visited[start] = true; queue[0] = start;
                    int head = 0, tail = 1, x1 = x, x2 = x, y1 = y, y2 = y;
                    while (head < tail)
                    {
                        int current = queue[head++], cx = current % image.Width, cy = current / image.Width;
                        x1 = Math.Min(x1, cx); x2 = Math.Max(x2, cx); y1 = Math.Min(y1, cy); y2 = Math.Max(y2, cy);
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = cx + dx, ny = cy + dy;
                                if (nx < 0 || ny < 0 || nx >= image.Width || ny >= image.Height) continue;
                                int next = ny * image.Width + nx;
                                if (!visited[next] && pixels.Status(nx, ny, color)) { visited[next] = true; queue[tail++] = next; }
                            }
                    }
                    int w = x2 - x1 + 1, h = y2 - y1 + 1;
                    double fill = (double)tail / (w * h);
                    if (w >= 9 && h >= 9 && w < image.Height * .32 && h < image.Height * .32 && w / (double)h > .78 && w / (double)h < 1.28 && fill > .40 && fill < .89)
                    {
                        int cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;
                        if (color == DotColor.Green || (pixels.Yellow(cx - w / 5, cy) && pixels.Yellow(cx + w / 5, cy))) count++;
                    }
                }
            return count;
        }
        internal static List<QuizChoice> Choices(Bitmap image, QuizTextPage text)
        {
            QuizPixels pixels = new QuizPixels(image);
            List<QuizChoice> result = new List<QuizChoice>();
            foreach (QuizText word in text.Words)
            {
                string label = Normalize(word.Text).ToUpperInvariant();
                if (!Regex.IsMatch(label, "^[A-H]$") || word.Bounds.Left > image.Width * .22) continue;
                Rectangle box = FindCircle(pixels, word.Bounds);
                if (box.IsEmpty || result.Any(o => Math.Abs(o.Center.Y - (box.Top + box.Height / 2)) < box.Height / 2)) continue;
                result.Add(new QuizChoice { Label = label, Bounds = box, Selected = IsSelected(pixels, box) });
            }
            AddTruthChoices(pixels, text, result);
            var markers = text.Lines.Select(l => Marker(l, 0, image.Width)).Where(m => m != null).OrderBy(m => m.Top).ToArray();
            foreach (QuizMarker judge in markers.Where(m => m.Type == "judge"))
            {
                QuizMarker next = markers.FirstOrDefault(m => m.Top > judge.Top + 25);
                int top = judge.Top + judge.Height + 1, bottom = next == null ? image.Height - 1 : next.Top - 5;
                QuizChoice[] known = result.Where(o => o.Center.Y >= top && o.Center.Y < bottom).ToArray();
                QuizChoice[] letters = known.Where(o => !QuizChoice.IsTruthLabel(o.Label)).ToArray();
                bool textOnly = letters.Length == 0;
                if (textOnly ? known.Length != 1 : letters.Length != 1) continue;
                QuizChoice anchor = textOnly ? known[0] : letters[0];
                if (!textOnly && anchor.Label != "A" && anchor.Label != "B") continue;
                Rectangle[] controls = JudgeCircles(pixels, top, bottom);
                if (controls.Length != 2) continue;
                int ax = controls[0].Left + controls[0].Width / 2, bx = controls[1].Left + controls[1].Width / 2;
                if (Math.Abs(ax - bx) > 10 || Math.Abs(controls[0].Width - controls[1].Width) > 10 || controls[1].Top <= controls[0].Bottom + 8) continue;
                int knownIndex = Array.FindIndex(controls, c => c.Contains(anchor.Center));
                if (knownIndex < 0 || (!textOnly && anchor.Label != (knownIndex == 0 ? "A" : "B"))) continue;
                Rectangle missing = controls[1 - knownIndex];
                string missingLabel;
                if (textOnly) missingLabel = anchor.Label == "对" ? "错" : "对";
                else
                {
                    missingLabel = anchor.Label == "A" ? "B" : "A";
                    result.RemoveAll(o => QuizChoice.IsTruthLabel(o.Label) && missing.Contains(o.Center));
                }
                result.Add(new QuizChoice { Label = missingLabel, Bounds = missing, Selected = IsSelected(pixels, missing) });
            }
            return result.OrderBy(c => c.Center.Y).ToList();
        }
        private static void AddTruthChoices(QuizPixels pixels, QuizTextPage text, List<QuizChoice> result)
        {
            foreach (QuizText row in text.Lines)
            {
                string label;
                switch (Normalize(row.Text))
                {
                    case "对": case "正确": label = "对"; break;
                    case "错": case "错误": label = "错"; break;
                    default: continue;
                }
                if (row.Bounds.Left > pixels.Width * .30) continue;
                int y = row.Bounds.Top + row.Bounds.Height / 2;
                Rectangle[] circles = JudgeCircles(pixels, Math.Max(0, y - 72), Math.Min(pixels.Height, y + 72));
                Rectangle[] matches = circles.Where(c => Math.Abs(c.Top + c.Height / 2 - y) <= Math.Max(8, c.Height / 3) &&
                    row.Bounds.Left >= c.Left - 4 && row.Bounds.Left <= c.Right + c.Width).ToArray();
                if (matches.Length != 1 || result.Any(o => matches[0].Contains(o.Center))) continue;
                Rectangle circle = matches[0];
                result.Add(new QuizChoice { Label = label, Bounds = circle, Selected = IsSelected(pixels, circle) });
            }
        }
        internal static Rectangle[] OptionCircles(Bitmap image, int top, int bottom)
        { return JudgeCircles(new QuizPixels(image), Math.Max(0, top), Math.Min(image.Height, bottom)); }
        private static Rectangle[] JudgeCircles(QuizPixels pixels, int top, int bottom)
        {
            int width = Math.Max(1, (int)(pixels.Width * .22)), height = bottom - top;
            List<Rectangle> circles = new List<Rectangle>();
            if (height <= 16) return circles.ToArray();
            bool[] visited = new bool[width * height];
            int[] queue = new int[visited.Length];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int start = y * width + x;
                    if (visited[start] || !pixels.Blue(x, y + top)) continue;
                    int head = 0, tail = 1, x1 = x, x2 = x, y1 = y, y2 = y;
                    visited[start] = true; queue[0] = start;
                    while (head < tail)
                    {
                        int current = queue[head++], cx = current % width, cy = current / width;
                        x1 = Math.Min(x1, cx); x2 = Math.Max(x2, cx); y1 = Math.Min(y1, cy); y2 = Math.Max(y2, cy);
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = cx + dx, ny = cy + dy;
                                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                                int next = ny * width + nx;
                                if (!visited[next] && pixels.Blue(nx, ny + top)) { visited[next] = true; queue[tail++] = next; }
                            }
                    }
                    int w = x2 - x1 + 1, h = y2 - y1 + 1;
                    if (w < 14 || h < 14 || w > 130 || h > 130 || w / (double)h < .82 || w / (double)h > 1.22 ||
                        x1 < 1 || x2 >= width - 1 || y1 < 1 || y2 >= height - 1) continue;
                    int cx0 = (x1 + x2) / 2, cy0 = (y1 + y2) / 2 + top, matches = 0;
                    double radius = (w + h) / 4.0;
                    for (int i = 0; i < 32; i++)
                    {
                        double angle = i * Math.PI / 16;
                        for (int delta = -2; delta <= 1; delta++)
                            if (pixels.Blue(cx0 + (int)Math.Round(Math.Cos(angle) * (radius + delta)), cy0 + (int)Math.Round(Math.Sin(angle) * (radius + delta))))
                            { matches++; break; }
                    }
                    if (matches >= 26) circles.Add(Rectangle.FromLTRB(x1, y1 + top, x2 + 1, y2 + top + 1));
                }
            return circles.OrderBy(c => c.Top).ToArray();
        }
        private static Rectangle FindCircle(QuizPixels pixels, Rectangle letter)
        {
            int centerX = letter.Left + letter.Width / 2, centerY = letter.Top + letter.Height / 2;
            int tolerance = Math.Max(2, Math.Min(6, letter.Height / 5));
            int min = Math.Max(8, (int)(letter.Height * .65)), max = Math.Min(75, (int)(letter.Height * 2.2));
            Rectangle best = Rectangle.Empty; double bestScore = 0;
            for (int cy = centerY - tolerance; cy <= centerY + tolerance; cy += 2)
                for (int cx = centerX - tolerance; cx <= centerX + tolerance; cx += 2)
                    for (int radius = min; radius <= max; radius++)
                    {
                        if (cx - radius < 1 || cy - radius < 1 || cx + radius >= pixels.Width - 1 || cy + radius >= pixels.Height - 1) continue;
                        int corners = 0;
                        foreach (int sx in new int[] { -1, 1 })
                            foreach (int sy in new int[] { -1, 1 })
                                if (pixels.Blue(cx + sx * radius * 9 / 10, cy + sy * radius * 9 / 10)) corners++;
                        if (corners > 1) continue;
                        int matches = 0;
                        for (int i = 0; i < 32; i++)
                        {
                            double angle = i * Math.PI / 16;
                            bool found = false;
                            for (int dr = -1; dr <= 1; dr++)
                                if (pixels.Blue(cx + (int)Math.Round(Math.Cos(angle) * (radius + dr)), cy + (int)Math.Round(Math.Sin(angle) * (radius + dr)))) { found = true; break; }
                            if (found) matches++;
                        }
                        double score = matches / 32.0;
                        if (score < .78) continue;
                        // Prefer a well-centered outer circumference, never the dark letter itself.
                        double ranking = score + radius * .002 - (Math.Abs(cx - centerX) + Math.Abs(cy - centerY)) * .002;
                        if (ranking > bestScore) { bestScore = ranking; best = new Rectangle(cx - radius, cy - radius, radius * 2 + 1, radius * 2 + 1); }
                    }
            return best;
        }
        internal static QuizChoice ChoiceAt(Bitmap image, Rectangle circle, string label)
        { return new QuizChoice { Label = label, Bounds = circle, Selected = IsSelected(new QuizPixels(image), circle) }; }
        private static bool IsSelected(QuizPixels pixels, Rectangle circle)
        {
            int blue = 0, count = 0, radius = circle.Width * 3 / 10;
            int cx = circle.Left + circle.Width / 2, cy = circle.Top + circle.Height / 2;
            for (int y = -radius; y <= radius; y += 2)
                for (int x = -radius; x <= radius; x += 2)
                {
                    if (x * x + y * y > radius * radius) continue;
                    Color c = pixels.At(cx + x, cy + y); count++;
                    if (c.B > 170 && c.B - c.R > 60 && c.G < 225) blue++;
                }
            return count > 0 && blue / (double)count > .35;
        }
        internal static int ScrollShift(QuizPage before, QuizPage after, int direction)
        {
            var first = before.Text.Lines.GroupBy(l => Anchor(l.Text)).Where(g => g.Key.Length >= 3 && g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            var second = after.Text.Lines.GroupBy(l => Anchor(l.Text)).Where(g => g.Key.Length >= 3 && g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            List<Tuple<int, int>> shifts = new List<Tuple<int, int>>();
            foreach (var pair in first)
            {
                QuizText line;
                if (!second.TryGetValue(pair.Key, out line)) continue;
                int shift = pair.Value.Bounds.Top - line.Bounds.Top;
                if (Math.Abs(pair.Value.Bounds.Left - line.Bounds.Left) > 12 || Math.Abs(shift) > before.Image.Height * .85 || shift * direction < -5) continue;
                shifts.Add(Tuple.Create(shift, pair.Key.Length));
            }
            int pixelShift;
            if (shifts.Count == 0)
            {
                if (PixelShift(before.Image, after.Image, direction, out pixelShift)) return pixelShift;
                throw new QuizPauseException("滚动前后没有可靠的重叠内容，已暂停以避免题目错位。");
            }
            var cluster = shifts.OrderByDescending(s => shifts.Count(v => Math.Abs(v.Item1 - s.Item1) <= 4)).ThenByDescending(s => s.Item2).First();
            var agreement = shifts.Where(s => Math.Abs(s.Item1 - cluster.Item1) <= 4).OrderBy(s => s.Item1).ToArray();
            if (agreement.Length < 2 && agreement[0].Item2 < 12)
            {
                if (PixelShift(before.Image, after.Image, direction, out pixelShift)) return pixelShift;
                throw new QuizPauseException("滚动内容锚点不足，请调整测验内容区域后重试。");
            }
            return agreement[agreement.Length / 2].Item1;
        }
        private static int Bits(ulong value)
        {
            value -= (value >> 1) & 0x5555555555555555UL;
            value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
            value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((value * 0x0101010101010101UL) >> 56);
        }
        private static ulong[][] InkRows(Bitmap image)
        {
            QuizPixels pixels = new QuizPixels(image);
            int columns = (int)(image.Width * .90) / 2, words = (columns + 63) / 64;
            ulong[][] rows = new ulong[image.Height][];
            for (int y = 0; y < image.Height; y++)
            {
                rows[y] = new ulong[words];
                for (int x = 0; x < columns; x++)
                {
                    Color c = pixels.At(16 + x * 2, y);
                    if (c.R * 299 + c.G * 587 + c.B * 114 < 200000) rows[y][x / 64] |= 1UL << (x % 64);
                }
            }
            return rows;
        }
        private static bool PixelShift(Bitmap first, Bitmap second, int direction, out int result)
        {
            result = 0;
            if (first.Size != second.Size) return false;
            ulong[][] a = InkRows(first), b = InkRows(second);
            int height = (int)(first.Height * .80), maximum = (int)(height * .70);
            double best = 1, runnerUp = 1; int bestShift = 0;
            int low = direction > 0 ? 0 : -maximum, high = direction < 0 ? 0 : maximum;
            if (direction == 0) { low = -5; high = 5; }
            for (int shift = low; shift <= high; shift++)
            {
                int top = Math.Max(0, shift), bottom = Math.Min(height, height + shift), different = 0, union = 0;
                for (int y = top; y < bottom; y += 2)
                {
                    for (int w = 0; w < a[y].Length; w++)
                    {
                        different += Bits(a[y][w] ^ b[y - shift][w]);
                        union += Bits(a[y][w] | b[y - shift][w]);
                    }
                    if (union > 400 && different > union * .20) break;
                }
                if (union < 160) continue;
                double score = different / (double)union;
                if (score < best)
                {
                    if (Math.Abs(shift - bestShift) > 3) runnerUp = best;
                    best = score; bestShift = shift;
                }
                else if (Math.Abs(shift - bestShift) > 3) runnerUp = Math.Min(runnerUp, score);
            }
            if (best > .06 || runnerUp - best < .025) return false;
            result = bestShift; return true;
        }
    }

    internal sealed class QuizDocument : IDisposable
    {
        private sealed class Strip { public int Top; public Bitmap Image; }
        private readonly List<Strip> strips = new List<Strip>();
        internal readonly List<QuizMarker> Markers = new List<QuizMarker>();
        private readonly List<QuizChoice> choices = new List<QuizChoice>();
        private readonly List<QuizBlank> blanks = new List<QuizBlank>();
        private readonly List<QuizChoiceReading> choiceReadings = new List<QuizChoiceReading>();
        private int currentTop, currentBottom;
        internal int Expected, MaxBottom, Width;
        internal int? EndAt;
        internal bool BottomConfirmed;
        internal string PendingReason;
        internal string PendingLocalFailure;
        internal void Add(QuizPage page)
        {
            Width = page.Image.Width; MaxBottom = Math.Max(MaxBottom, page.Bottom);
            currentTop = page.Top; currentBottom = page.Bottom;
            if (Expected == 0) Expected = QuizVision.Total(page.Text);
            Strip previous = strips.FirstOrDefault(s => Math.Abs(s.Top - page.Top) <= 3);
            if (previous != null) { previous.Image.Dispose(); strips.Remove(previous); }
            strips.Add(new Strip { Top = page.Top, Image = (Bitmap)page.Image.Clone() });
            if (strips.Sum(s => (long)s.Image.Width * s.Image.Height) > 80000000)
                throw new QuizPauseException("当前单题跨页过长，已暂停以限制内存占用。");
            int? footerBoundary = page.FooterBoundary;
            if (footerBoundary.HasValue) EndAt = footerBoundary;
            foreach (QuizText line in page.Text.Lines)
            {
                QuizMarker marker = QuizVision.Marker(line, page.Top, page.Image.Width);
                if (marker != null && !Markers.Any(m => m.Type == marker.Type && m.Number == marker.Number && Math.Abs(m.Top - marker.Top) < 25)) Markers.Add(marker);
            }
            Markers.Sort((a, b) => a.Top.CompareTo(b.Top));
            foreach (QuizChoice option in page.Options)
            {
                Rectangle global = option.Bounds; global.Offset(0, page.Top);
                int x = global.Left + global.Width / 2, y = global.Top + global.Height / 2;
                QuizChoice previousChoice = choices.FirstOrDefault(o => o.Label == option.Label && Math.Abs(o.Center.Y - y) <= 6);
                if (previousChoice != null) { previousChoice.Selected = option.Selected; previousChoice.Bounds = global; continue; }
                if (QuizChoice.IsTruthLabel(option.Label))
                {
                    if (choices.Any(o => !QuizChoice.IsTruthLabel(o.Label) && Math.Abs(o.Center.X - x) <= 12 && Math.Abs(o.Center.Y - y) <= 6)) continue;
                }
                else choices.RemoveAll(o => QuizChoice.IsTruthLabel(o.Label) && Math.Abs(o.Center.X - x) <= 12 && Math.Abs(o.Center.Y - y) <= 6);
                choices.Add(new QuizChoice { Label = option.Label, Bounds = global, Selected = option.Selected });
            }
            foreach (QuizBlank blank in page.Blanks)
            {
                Rectangle global = blank.Bounds; global.Offset(0, page.Top);
                QuizBlank previousBlank = blanks.FirstOrDefault(b => b.Index == blank.Index && Math.Abs(b.Bounds.Top - global.Top) <= 8);
                if (previousBlank != null) blanks.Remove(previousBlank);
                blanks.Add(new QuizBlank { Index = blank.Index, Bounds = global, Value = blank.Value });
            }
            foreach (QuizChoiceReading reading in page.Text.ChoiceReadings)
            {
                Rectangle global = reading.Bounds; global.Offset(0, page.Top);
                choiceReadings.RemoveAll(r => Math.Abs(r.Bounds.Top - global.Top) <= 8 && Math.Abs(r.Bounds.Left - global.Left) <= 8);
                choiceReadings.Add(new QuizChoiceReading { Bounds = global, Label = reading.Label });
            }
        }
        internal QuizQuestion Next(HashSet<string> completed)
        {
            PendingReason = null; PendingLocalFailure = null;
            QuizMarker marker = Markers.FirstOrDefault(m => !completed.Contains(m.Id));
            if (marker == null) return null;
            if (marker.Type == "unsupported") throw new QuizPauseException("第 " + marker.Number + " 题不属于当前支持的选择或填空题，请人工处理。");
            QuizMarker next = Markers.FirstOrDefault(m => m.Top > marker.Top + 25);
            int? bottom = next == null ? EndAt : next.Top - Math.Max(8, next.Height * 2 / 3);
            if (!bottom.HasValue) { PendingReason = "第 " + marker.Number + " 题尚未识别到完整下边界"; return null; }
            int top = Math.Max(0, marker.Top - Math.Max(8, marker.Height * 2 / 3));
            if (marker.Type == "fill")
            {
                var fields = blanks.Where(b => b.Bounds.Top > marker.Top + marker.Height && b.Bounds.Bottom < bottom.Value).OrderBy(b => b.Bounds.Top).ToList();
                if (fields.Count == 0 || fields.Select(b => b.Index).Distinct().Count() != fields.Count || !fields.Select((b, i) => b.Index == i + 1).All(v => v))
                {
                    PendingReason = "第 " + marker.Number + " 题填空框尚未识别完整";
                    return null;
                }
                return new QuizQuestion { Id = marker.Id, Type = marker.Type, Number = marker.Number, Top = top, Bottom = bottom.Value, Blanks = fields };
            }
            var options = choices.Where(o => o.Center.Y > marker.Top + marker.Height && o.Bounds.Bottom < bottom.Value &&
                (marker.Type == "judge" || !QuizChoice.IsTruthLabel(o.Label))).OrderBy(o => o.Center.Y).ToList();
            bool lettered = options.Count >= 2 && options.Select((o, i) => o.Label == ((char)('A' + i)).ToString()).All(x => x);
            bool truth = marker.Type == "judge" && options.Count == 2 && options.Any(o => o.Label == "对") && options.Any(o => o.Label == "错");
            QuizChoiceReading[] observed = choiceReadings.Where(r => r.Bounds.Top > marker.Top + marker.Height && r.Bounds.Bottom < bottom.Value).ToArray();
            int unread = observed.Count(r => String.IsNullOrEmpty(r.Label));
            if ((!lettered && !truth) || unread > 0)
            {
                PendingReason = "第 " + marker.Number + " 题选项尚未识别完整" + (unread == 0 ? "" : "（" + unread + " 个圆圈字母局部复读后仍不可靠）");
                // A complete visible block needs better recognition, not another downward wheel.
                if (observed.Length >= 2 && marker.Top >= currentTop && bottom.Value < currentBottom - 3)
                    PendingLocalFailure = PendingReason + "；题块已完整显示，已停止下滑。已识别：" +
                        (options.Count == 0 ? "无" : String.Join("、", options.Select(o => o.Label))) + "。请检查识别截图。";
                return null;
            }
            return new QuizQuestion { Id = marker.Id, Type = marker.Type, Number = marker.Number, Top = top, Bottom = bottom.Value, Options = options };
        }
        internal void InvalidateFrom(int top)
        {
            // A strip may contain old pixels above/below the viewport; never reuse any such strip for this question.
            foreach (Strip strip in strips.Where(s => s.Top + s.Image.Height > top).ToArray())
            { strip.Image.Dispose(); strips.Remove(strip); }
            Markers.RemoveAll(m => m.Top >= top);
            choices.RemoveAll(c => c.Bounds.Bottom > top);
            blanks.RemoveAll(b => b.Bounds.Bottom > top);
            choiceReadings.RemoveAll(r => r.Bounds.Bottom > top);
            MaxBottom = strips.Count == 0 ? 0 : strips.Max(s => s.Top + s.Image.Height);
            EndAt = null; BottomConfirmed = false; PendingReason = PendingLocalFailure = null;
        }
        internal Bitmap Render(QuizQuestion question)
        {
            int height = question.Bottom - question.Top;
            if (height < 50 || height > 12000) throw new QuizPauseException("题块尺寸异常，请人工检查该题。");
            Bitmap bitmap = new Bitmap(Width, height, PixelFormat.Format24bppRgb);
            bool[] covered = new bool[height];
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                foreach (Strip strip in strips)
                {
                    int top = Math.Max(question.Top, strip.Top), bottom = Math.Min(question.Bottom, strip.Top + strip.Image.Height);
                    if (bottom <= top) continue;
                    graphics.DrawImage(strip.Image, new Rectangle(0, top - question.Top, Width, bottom - top), new Rectangle(0, top - strip.Top, Width, bottom - top), GraphicsUnit.Pixel);
                    for (int y = top; y < bottom; y++) covered[y - question.Top] = true;
                }
            }
            if (covered.Any(c => !c)) { bitmap.Dispose(); throw new QuizNeedsMoreException("题块有未采集的空隙"); }
            return bitmap;
        }
        internal void Prune(int before)
        {
            foreach (Strip strip in strips.Where(s => s.Top + s.Image.Height < before).ToArray()) { strip.Image.Dispose(); strips.Remove(strip); }
        }
        internal void Reset()
        {
            foreach (Strip strip in strips) strip.Image.Dispose();
            strips.Clear(); Markers.Clear(); choices.Clear(); blanks.Clear(); choiceReadings.Clear();
            Expected = MaxBottom = Width = currentTop = currentBottom = 0; EndAt = null; BottomConfirmed = false; PendingReason = PendingLocalFailure = null;
        }
        public void Dispose() { foreach (Strip strip in strips) strip.Image.Dispose(); }
    }
}
