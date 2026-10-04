namespace ClassicMac.Tests;

// Damaged input for the mutation tests (PLAN "Hostile input"): seeded mutants of a valid input (bytes flipped, a run
// zeroed or set, the end cut off), and a runner that allows a reader only to report the damage or refuse the input with
// InvalidDataException or EndOfStreamException; any other exception, or a run past its time limit, is a failure.
internal static class Mutations
{
    /// <summary>Mutants per input: 100, or CLASSICMAC_MUTANTS for a deeper run.</summary>
    public static int PerInput(int standard = 100) =>
        int.TryParse(Environment.GetEnvironmentVariable("CLASSICMAC_MUTANTS"), out var count) ? count : standard;

    public static byte[] Mutate(byte[] bytes, Random random)
    {
        var mutant = bytes.ToArray();
        if (mutant.Length == 0)
        {
            return mutant;
        }

        switch (random.Next(3))
        {
            case 0:
                for (var n = random.Next(1, 9); n > 0; n--)
                {
                    mutant[random.Next(mutant.Length)] ^= (byte)random.Next(1, 256);
                }

                return mutant;
            case 1:
                int at = random.Next(mutant.Length), length = Math.Min(mutant.Length - at, random.Next(1, 64));
                mutant.AsSpan(at, length).Fill((byte)(random.Next(2) == 0 ? 0 : 0xFF));
                return mutant;
            default:
                return mutant[..random.Next(mutant.Length)];
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> on a mutant; adds a failure for an exception other than the two allowed, or for a run
    /// longer than <paramref name="limit"/> (left running: a hang cannot be stopped).
    /// </summary>
    public static void Run(string label, Action read, ICollection<string> failures, TimeSpan? limit = null)
    {
        var task = Task.Run(read);
        try
        {
            if (!task.Wait(limit ?? TimeSpan.FromSeconds(10)))
            {
                failures.Add($"{label}: still running after {(limit ?? TimeSpan.FromSeconds(10)).TotalSeconds:0} s");
            }
        }
        catch (AggregateException e) when (e.InnerException is InvalidDataException or EndOfStreamException)
        {
        }
        catch (AggregateException e)
        {
            var inner = e.InnerException!;
            failures.Add($"{label}: {inner.GetType().Name}: {inner.Message} at {inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        }
    }
}
