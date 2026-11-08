using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace CoursePilot
{
    internal sealed class CourseTaskTarget
    {
        internal DotColor State;
        internal Rectangle Task, Video;
    }
    internal static class CourseLocator
    {
        internal static QuizText[] TaskLabels(QuizTextPage text, int width)
        {
            List<QuizText> labels = new List<QuizText>();
            foreach (QuizText line in text.Lines)
            {
                string value = QuizVision.Normalize(line.Text);
                int start = value.IndexOf("任务点", StringComparison.Ordinal);
                if (start < 0 || start > 1 || line.Bounds.Left >= width * .35) continue;
                QuizText[] words = text.Words.Where(w => Math.Abs(w.Bounds.Top + w.Bounds.Height / 2 - (line.Bounds.Top + line.Bounds.Height / 2)) <= Math.Max(4, line.Bounds.Height / 2)).OrderBy(w => w.Bounds.Left).ToArray();
                Rectangle bounds = Rectangle.Empty;
                for (int i = 0; i < words.Length && bounds.IsEmpty; i++)
                {
                    string label = ""; Rectangle span = words[i].Bounds;
                    for (int j = i; j < words.Length && label.Length < 3; j++)
                    {
                        label += QuizVision.Normalize(words[j].Text); span = Rectangle.Union(span, words[j].Bounds);
                        if (label == "任务点") { bounds = span; break; }
                    }
                }
                if (bounds.IsEmpty && start == 0) bounds = line.Bounds;
                if (!bounds.IsEmpty) labels.Add(new QuizText { Text = "任务点", Bounds = bounds });
            }
            return labels.OrderBy(l => l.Bounds.Top).ToArray();
        }
        internal static List<CourseTaskTarget> Find(Bitmap screen, QuizTextPage text, Rectangle content, Regions regions)
        {
            List<CourseTaskTarget> targets = new List<CourseTaskTarget>();
            QuizText[] labels = TaskLabels(text, content.Width);
            for (int i = 0; i < labels.Length; i++)
            {
                Rectangle line = labels[i].Bounds; int h = Math.Max(12, line.Height);
                Rectangle local = Rectangle.Intersect(new Rectangle(0, 0, content.Width, content.Height),
                    Rectangle.FromLTRB(line.Left - h * 3, line.Top - h, line.Left + h / 2, line.Bottom + h));
                if (local.Width < 8 || local.Height < 8) continue;
                Rectangle task = local; task.Offset(content.Location);
                List<Dot> dots;
                using (Bitmap crop = screen.Clone(task, PixelFormat.Format24bppRgb)) dots = Recognition.FindTaskDots(crop);
                dots = dots.Where(d => Math.Abs(task.Top + d.Center.Y - (content.Top + line.Top + line.Height / 2)) <= h).ToList();
                if (dots.Count != 1) continue;
                Rectangle exact = dots[0].Bounds; exact.Offset(task.Location); exact.Inflate(Math.Max(5, h / 2), Math.Max(5, h / 2));
                exact = Rectangle.Intersect(content, exact);
                CourseTaskTarget target = new CourseTaskTarget { State = dots[0].Color, Task = exact, Video = regions.Video.ToPixels(screen.Size) };
                targets.Add(target);
            }
            // Calibrated task region plus course text; button appearance never controls the click location.
            if (labels.Length == 0 && text.Lines.Any(l => QuizVision.Normalize(l.Text).Length >= 2))
            {
                Rectangle task = regions.Task.ToPixels(screen.Size), video = regions.Video.ToPixels(screen.Size);
                using (Bitmap crop = screen.Clone(task, PixelFormat.Format24bppRgb))
                {
                    List<Dot> dots = Recognition.FindTaskDots(crop);
                    if (dots.Count == 1)
                    {
                        targets.Add(new CourseTaskTarget { State = dots[0].Color, Task = task, Video = video });
                    }
                }
            }
            return targets;
        }
    }
}
