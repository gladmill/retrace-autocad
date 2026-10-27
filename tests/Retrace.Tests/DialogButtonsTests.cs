using Retrace.Core;

namespace Retrace.Tests;

public class DialogButtonsTests
{
    [Theory]
    [InlineData("OK", null, "ok")]
    [InlineData("&OK", null, "ok")]
    [InlineData("Okay", null, "ok")]
    [InlineData("Cancel", null, "cancel")]
    [InlineData("  &Cancel ", null, "cancel")]
    [InlineData(null, "btnOk", "ok")]
    [InlineData("", "OkButton", "ok")]
    [InlineData(null, "ButtonCancel", "cancel")]
    [InlineData("Browse...", "btnBrowse", null)]
    [InlineData("Cancelled items", null, null)]
    [InlineData(null, null, null)]
    [InlineData("Apply", "CancelButton", "cancel")]   // name unknown: the id decides
    public void Role_maps_names_and_automation_ids(string? name, string? id, string? role) => Assert.Equal(role, DialogButtons.Role(name, id));
}
