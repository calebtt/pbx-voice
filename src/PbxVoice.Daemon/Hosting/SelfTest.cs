using System.Diagnostics;
using System.Runtime.InteropServices;
using PbxVoice.Speech;

namespace PbxVoice.Hosting;

/// <summary>
/// <c>pbx-voice selftest</c>: checks that this host can run the daemon's local pieces (platform,
/// ONNX Runtime, and Silero VAD inference) without touching SIP or xAI. Meant for the host probe
/// before deploying (PR-DEP-1, PR-DEP-2).
/// </summary>
internal static class SelfTest
{
    public static int Run(TextWriter output)
    {
        output.WriteLine($"os:       {RuntimeInformation.OSDescription}");
        output.WriteLine($"arch:     {RuntimeInformation.OSArchitecture} ({RuntimeInformation.RuntimeIdentifier})");
        output.WriteLine($"runtime:  {RuntimeInformation.FrameworkDescription}");
        output.WriteLine($"state:    {StatePaths.FromEnvironment().Root}");

        SileroClassifier vad;
        try
        {
            vad = SileroClassifier.Load();
        }
        catch (Exception ex)
        {
            output.WriteLine($"silero:   FAILED to load ({ex.GetType().Name}: {ex.Message})");
            return 1;
        }

        using (vad)
        {
            // Two seconds of silence and two of low noise: inference must run and stay below the
            // speech threshold. Speech itself is covered by the unit tests' recorded fixtures.
            var rng = new Random(7);
            var window = new short[vad.WindowSamples];
            float peak = 0;
            int windows = 2 * 2 * 8000 / vad.WindowSamples;
            var watch = Stopwatch.StartNew();
            vad.Reset();
            for (int w = 0; w < windows; w++)
            {
                for (int i = 0; i < window.Length; i++)
                    window[i] = w < windows / 2 ? (short)0 : (short)rng.Next(-100, 100);
                peak = Math.Max(peak, vad.Probability(window));
            }
            double perWindowMs = watch.Elapsed.TotalMilliseconds / windows;
            output.WriteLine($"silero:   loaded in {vad.LoadTime.TotalMilliseconds:0} ms; {perWindowMs:0.00} ms per 32 ms window; peak on silence/noise {peak:0.000}");
            if (peak >= SpeechDetector.StartThreshold)
            {
                output.WriteLine("silero:   FAILED: silence scored as speech");
                return 1;
            }
        }
        output.WriteLine("ok");
        return 0;
    }
}
