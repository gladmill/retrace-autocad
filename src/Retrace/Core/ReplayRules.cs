namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). Small rules of the replayer that need no AutoCAD to test.
public static class ReplayRules
{
    /// <summary>True when nothing is left to run from step index <paramref name="i"/> (0-based): there are no steps, or only dialog steps. A CLOSE
    /// that ends a drawing can be followed by the "Save changes?" dialog the drawing's own command showed: those belong to the closed drawing.</summary>
    public static bool NothingLeftToRun(IReadOnlyList<Step> steps, int i)
    {
        for (var k = Math.Max(0, i); k < steps.Count; k++)
            if (steps[k].Kind != "dialog") return false;
        return true;
    }
}
