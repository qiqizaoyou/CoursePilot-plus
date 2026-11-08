using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class FixedFlowDesktop : IDesktop, IDisposable
        {
            internal readonly CourseLayoutFixture Lesson = new CourseLayoutFixture { Duration = 40000, HideFirstPlay = true };
            internal readonly List<Point> Clicks = new List<Point>();
            internal readonly List<long> ClickTimes = new List<long>();
            internal int Stage, DirectoryScrolls;
            public Bitmap Capture() { return Stage == 1 ? Lesson.Capture() : CountDirectory(0, Stage == 2); }
            public void Click(Point point)
            {
                Clicks.Add(point); ClickTimes.Add(Lesson.Clock.Milliseconds);
                if (Stage == 0) { Stage = 1; return; }
                Rectangle back = Lesson.Regions.Back.ToPixels(Lesson.Size);
                if (point == new Point(back.Left + back.Width / 2, back.Top + back.Height / 2)) { Stage = 2; return; }
                Lesson.Click(point);
            }
            public void Scroll(Point point, int notches)
            { if (Stage == 1) throw new Exception("Course playback must not scroll"); DirectoryScrolls++; }
            public void Dispose() { Lesson.Dispose(); }
        }
        private sealed class FixedFlowQuiz : IQuizHandler, ICoursePageSource
        {
            private readonly CourseLayoutFixture lesson;
            internal FixedFlowQuiz(CourseLayoutFixture lesson) { this.lesson = lesson; }
            public async Task<bool> TryRun(Bitmap screen, CancellationToken token)
            {
                CoursePageEvidence result = await lesson.InspectCourse(screen, token);
                if (result.QuizClues) throw new Exception("Unexpected quiz in course fixture"); return false;
            }
            public Task<CoursePageEvidence> InspectCourse(Bitmap screen, CancellationToken token) { return lesson.InspectCourse(screen, token); }
            public void ReportCourseIssue(Bitmap screen, string reason) { lesson.ReportCourseIssue(screen, reason); }
        }
        private static async Task TestFixedPlayback(string output)
        {
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_FIXED_CLICK_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
                using (Bitmap image = new Bitmap(reference))
                {
                    Regions regions = new Regions();
                    CoursePageEvidence result = await new CoursePageClassifier(regions, new WindowsQuizOcr()).Probe(image, CancellationToken.None);
                    Rectangle video = regions.Video.ToPixels(image.Size);
                    File.WriteAllLines(Path.Combine(output, "fixed-click-reference.txt"), new[] { result.Describe(), "saved region=" + video, "click=" + new Point(video.Left + video.Width / 2, video.Top + video.Height / 2) });
                    Assert(result.Kind == CoursePageKind.Video && !result.QuizClues && result.Tasks.Count == 1 && result.Tasks[0].State == DotColor.Yellow,
                        "user failure screenshot routes to video from its task point without identifying the play icon");
                    Assert(result.Tasks[0].Video == video, "user failure screenshot uses exactly the configured video crop");
                }
            using (FixedFlowDesktop desktop = new FixedFlowDesktop())
            {
                List<long> samples = new List<long>();
                Action<Bitmap, Bitmap, int, string> preview = delegate(Bitmap task, Bitmap video, int sample, string stage) { if (sample > 0) samples.Add(desktop.Lesson.Clock.Milliseconds); };
                Outcome result = await new CourseSession(desktop, desktop.Lesson.Regions, IgnoreLog, IgnorePreview, preview, desktop.Lesson.Clock,
                    new SessionTiming { Scroll = 1 }, new FixedFlowQuiz(desktop.Lesson), new CountReader(1)).Run(CancellationToken.None);
                Rectangle videoRegion = desktop.Lesson.Regions.Video.ToPixels(desktop.Lesson.Size);
                Assert(result == Outcome.BottomReached && desktop.ClickTimes.SequenceEqual(new long[] { 0, 5000, 45000 }),
                    "full count-one course flow waits 5 seconds, clicks once, and returns at the first green task check");
                Assert(desktop.Clicks[1] == new Point(videoRegion.Left + videoRegion.Width / 2, videoRegion.Top + videoRegion.Height / 2) && desktop.Lesson.Wheels.Count == 0,
                    "full course flow clicks the exact saved region center even with no play icon and never scrolls the lesson");
                Assert(samples.SequenceEqual(new long[] { 15000, 25000, 35000 }),
                    "fixed playback samples the saved video region exactly 10, 20 and 30 seconds after clicking");
            }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture { HideFirstPlay = true })
            {
                fixture.Regions.Video = new Region(.22, .27, .40, .30);
                Rectangle area = fixture.Regions.Video.ToPixels(fixture.Size);
                CourseStudyResult result = await CourseExplorerFor(fixture, new List<string>()).Run(CancellationToken.None);
                Assert(result == CourseStudyResult.Complete && fixture.Clicks.Single() == new Point(area.Left + area.Width / 2, area.Top + area.Height / 2),
                    "a custom saved video region controls the exact click instead of default coordinates or visual targets");
            }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture())
            {
                fixture.Done[0] = true;
                CourseStudyResult result = await CourseExplorerFor(fixture, new List<string>()).Run(CancellationToken.None);
                Assert(result == CourseStudyResult.Complete && fixture.Clicks.Count == 0 && fixture.Wheels.Count == 0,
                    "an already green task returns without clicking or scrolling");
            }
        }
    }
}
