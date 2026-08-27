namespace Bas.Api.Statements;

/// <summary>
/// Structural validation of an Australian Tax File Number.
///
/// <para>Moved verbatim from PracticeManager.Client, where it exists because client creation there
/// is two calls - POST the client, then PUT its tax details - and only the second validates the
/// TFN. A TFN Practice Manager rejects therefore leaves a fully-created client behind, and because
/// the sync ledger retries, every attempt orphans another one in the live practice.</para>
///
/// <para>Checking it here as well means the worker is told at the moment they type it, rather than
/// a quarter later when the reconciler tries to push. The check is the ATO's published algorithm:
/// exactly nine digits, weighted by 1,4,3,7,5,8,6,9,10, summing to a multiple of 11. It proves the
/// number is well-formed, not that it belongs to anyone.</para>
/// </summary>
public static class TfnValidator
{
    private static readonly int[] Weights = [1, 4, 3, 7, 5, 8, 6, 9, 10];

    /// <summary>Digits only - a worker may type spaces, and usually will.</summary>
    public static string Normalise(string? tfn) =>
        string.IsNullOrEmpty(tfn) ? "" : new string(tfn.Where(char.IsDigit).ToArray());

    /// <summary>
    /// True when <paramref name="tfn"/> is a structurally valid TFN. <paramref name="reason"/>
    /// explains the failure in terms a person can act on, and never contains the TFN itself.
    /// </summary>
    public static bool IsValid(string? tfn, out string reason)
    {
        var digits = Normalise(tfn);

        if (digits.Length == 0)
        {
            reason = "no TFN was supplied";
            return false;
        }

        if (digits.Length != 9)
        {
            reason = $"a TFN must be 9 digits, but this one has {digits.Length}";
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
            sum += (digits[i] - '0') * Weights[i];

        if (sum % 11 != 0)
        {
            reason = "the TFN failed the ATO checksum, so at least one digit is wrong";
            return false;
        }

        reason = "";
        return true;
    }
}

/// <summary>Structural validation of an Australian Business Number.</summary>
public static class AbnValidator
{
    private static readonly int[] Weights = [10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19];

    public static string Normalise(string? abn) =>
        string.IsNullOrEmpty(abn) ? "" : new string(abn.Where(char.IsDigit).ToArray());

    /// <summary>
    /// The ATO's ABN algorithm: eleven digits, subtract one from the first, weight by
    /// 10,1,3,5,7,9,11,13,15,17,19, and the sum must divide by 89.
    /// </summary>
    public static bool IsValid(string? abn, out string reason)
    {
        var digits = Normalise(abn);

        if (digits.Length == 0)
        {
            reason = "no ABN was supplied";
            return false;
        }

        if (digits.Length != 11)
        {
            reason = $"an ABN must be 11 digits, but this one has {digits.Length}";
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 11; i++)
        {
            var digit = digits[i] - '0';
            if (i == 0)
                digit -= 1;

            sum += digit * Weights[i];
        }

        if (sum % 89 != 0)
        {
            reason = "the ABN failed the ATO checksum, so at least one digit is wrong";
            return false;
        }

        reason = "";
        return true;
    }
}
