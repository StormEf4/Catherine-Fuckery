using System;

namespace InvestigationNightmares.Powers;

/// <summary>
/// Jiraiya's Garu: holds Catherine's grab button and taps "away from the wall" once per pull, the way a
/// player pulls a block out step by step. Timeline is in game milliseconds (the game clock runs faster
/// during the power, so it plays out in a blink of real time).
/// </summary>
public sealed class AutoPullMacro
{
    public int Pulls { get; }
    public double StepMs { get; }
    public double TotalMs => Pulls * StepMs + StepMs * 0.3;

    public AutoPullMacro(int pulls, double stepMs)
    {
        if (pulls < 1 || pulls > 10) throw new ArgumentOutOfRangeException(nameof(pulls));
        if (stepMs < 50) throw new ArgumentOutOfRangeException(nameof(stepMs));
        Pulls = pulls;
        StepMs = stepMs;
    }

    /// <summary>Which of Catherine's controls are held at a moment of the macro.</summary>
    public (bool grab, bool back, bool done) StateAt(double ms)
    {
        if (ms < 0) return (false, false, false);
        if (ms >= TotalMs) return (false, false, true);
        // Grab goes down first; each pull taps "back" in the middle of its step while grab stays held.
        double lead = StepMs * 0.15;
        if (ms < lead) return (true, false, false);
        double t = ms - lead;
        int step = (int)(t / StepMs);
        if (step >= Pulls) return (true, false, false);
        double within = t - step * StepMs;
        bool back = within >= StepMs * 0.1 && within < StepMs * 0.6;
        return (true, back, false);
    }
}
