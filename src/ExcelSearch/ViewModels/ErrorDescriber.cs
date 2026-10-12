using System.Data.Common;

namespace ExcelSearch.ViewModels;

/// <summary>Turns an exception into one friendly sentence for a dialog (the full exception goes to the log).</summary>
public static class ErrorDescriber
{
    public static string Describe(Exception ex)
    {
        // EF and SqlClient wrap each other, so look through the whole chain for a database exception.
        Exception? e = ex;
        while (e is not null)
        {
            if (e is DbException)
                return "The database could not be reached or rejected the request. Check that SQL Server is running " +
                       "and the connection settings are correct, then try again.\n\nDetails: " + e.Message;
            e = e.InnerException;
        }
        return "Something went wrong and the operation was not completed.\n\nDetails: " + ex.Message;
    }
}
