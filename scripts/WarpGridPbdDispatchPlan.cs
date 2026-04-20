using System;

namespace WarpGrid;

public enum WarpGridDispatchPhaseKind
{
    Prediction = 0,
    Solver = 1,
    Finalize = 2,
}

public readonly record struct WarpGridDispatchPhase(
    int SubStepIndex,
    int PassIndex,
    WarpGridDispatchPhaseKind Kind,
    bool ApplyEffectors)
{
    public bool IsPredictionPass => Kind == WarpGridDispatchPhaseKind.Prediction;
    public bool IsSolverPass => Kind == WarpGridDispatchPhaseKind.Solver;
    public bool IsFinalizePass => Kind == WarpGridDispatchPhaseKind.Finalize;
}

public static class WarpGridPbdDispatchPlan
{
    public static WarpGridDispatchPhase[] Build(int subSteps, int solverIterations)
    {
        if (subSteps <= 0)
            throw new ArgumentOutOfRangeException(nameof(subSteps), subSteps, "SubSteps must be greater than zero.");
        if (solverIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(solverIterations), solverIterations, "SolverIterations must be greater than zero.");

        var phases = new WarpGridDispatchPhase[subSteps * (solverIterations + 2)];
        int index = 0;
        for (int subStep = 0; subStep < subSteps; subStep++)
        {
            phases[index++] = new WarpGridDispatchPhase(subStep, 0, WarpGridDispatchPhaseKind.Prediction, true);
            for (int solverPass = 1; solverPass <= solverIterations; solverPass++)
                phases[index++] = new WarpGridDispatchPhase(subStep, solverPass, WarpGridDispatchPhaseKind.Solver, false);
            phases[index++] = new WarpGridDispatchPhase(subStep, solverIterations + 1, WarpGridDispatchPhaseKind.Finalize, false);
        }
        return phases;
    }
}
