using System;
using System.Collections.Generic;

namespace PrinterHelper.Documents
{
    /// <summary>
    /// Combines a batch of front sides and a batch of back sides into reading order,
    /// for feeders that scan one side only.
    /// </summary>
    public static class Duplex
    {
        /// <param name="fronts">Front sides in the order they were scanned.</param>
        /// <param name="backs">Back sides in the order they were scanned.</param>
        /// <param name="backsReversed">
        /// True when the backs came out last sheet first. That is what happens on feeders that drop
        /// sheets face down (like the M2085FW): the output stack already shows its back sides and is
        /// fed again as it is, so the last sheet goes in first.
        /// </param>
        public static List<T> Interleave<T>(IReadOnlyList<T> fronts, IReadOnlyList<T> backs, bool backsReversed)
        {
            if (fronts.Count != backs.Count)
                throw new ArgumentException("앞면과 뒷면의 쪽수가 다릅니다.");
            var n = fronts.Count;
            var result = new List<T>(n * 2);
            for (var i = 0; i < n; i++)
            {
                result.Add(fronts[i]);
                result.Add(backsReversed ? backs[n - 1 - i] : backs[i]);
            }
            return result;
        }
    }
}
