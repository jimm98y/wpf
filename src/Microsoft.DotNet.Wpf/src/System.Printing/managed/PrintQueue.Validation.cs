// PrintQueue.MergeAndValidatePrintTicket: the delta ticket's settings laid over the base ticket's.
//
// On Windows the print driver merges and then validates against the device's capabilities. There is
// no driver to consult here, so the merge is the whole of it: every setting the delta ticket states
// replaces the base ticket's, and the result is reported conflict-free.

using System.IO;
using System.Reflection;

namespace System.Printing
{
    public partial class PrintQueue
    {
        public ValidationResult MergeAndValidatePrintTicket(PrintTicket basePrintTicket, PrintTicket deltaPrintTicket) =>
            MergeAndValidatePrintTicket(basePrintTicket, deltaPrintTicket, PrintTicketScope.JobScope);

        public ValidationResult MergeAndValidatePrintTicket(PrintTicket basePrintTicket, PrintTicket deltaPrintTicket, PrintTicketScope scope)
        {
            ArgumentNullException.ThrowIfNull(basePrintTicket);
            PrintTicket merged = basePrintTicket.Clone();
            if (deltaPrintTicket != null)
            {
                foreach (PropertyInfo p in typeof(PrintTicket).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length != 0) continue;
                    object value = p.GetValue(deltaPrintTicket);
                    if (value != null) p.SetValue(merged, value);
                }
            }
            var stream = new MemoryStream();
            merged.SaveTo(stream);
            stream.Position = 0;
            return new ValidationResult(stream, ConflictStatus.NoConflict);
        }
    }
}
