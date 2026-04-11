using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public sealed class MsiInspectorService : IMsiInspectorService
{
    public Task<MsiMetadata?> InspectAsync(string msiPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(msiPath) || !File.Exists(msiPath))
        {
            return Task.FromResult<MsiMetadata?>(null);
        }

        return Task.Run<MsiMetadata?>(() => InspectInternal(msiPath), cancellationToken);
    }

    private static MsiMetadata InspectInternal(string msiPath)
    {
        var installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer");
        if (installerType is null)
        {
            return new MsiMetadata
            {
                InspectionWarning = "Windows Installer APIs are unavailable on this system."
            };
        }

        object? installerCom = null;
        object? databaseCom = null;

        try
        {
            installerCom = Activator.CreateInstance(installerType);
            if (installerCom is null)
            {
                return new MsiMetadata
                {
                    InspectionWarning = "Unable to initialize MSI inspection engine."
                };
            }

            databaseCom = installerType.InvokeMember(
                "OpenDatabase",
                BindingFlags.InvokeMethod,
                binder: null,
                target: installerCom,
                args: new object[] { msiPath, 0 });

            if (databaseCom is null)
            {
                return new MsiMetadata
                {
                    InspectionWarning = "Unable to open MSI database."
                };
            }

            return new MsiMetadata
            {
                ProductName = ReadProperty(databaseCom, "ProductName") ?? string.Empty,
                ProductCode = ReadProperty(databaseCom, "ProductCode") ?? string.Empty,
                ProductVersion = ReadProperty(databaseCom, "ProductVersion") ?? string.Empty,
                Manufacturer = ReadProperty(databaseCom, "Manufacturer") ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            return new MsiMetadata
            {
                InspectionWarning = $"Failed to inspect MSI metadata: {ex.Message}"
            };
        }
        finally
        {
            ReleaseComObject(databaseCom);
            ReleaseComObject(installerCom);
        }
    }

    private static string? ReadProperty(object databaseCom, string propertyName)
    {
        object? viewCom = null;
        object? recordCom = null;

        try
        {
            var query = $"SELECT `Value` FROM `Property` WHERE `Property`='{propertyName}'";

            viewCom = databaseCom.GetType().InvokeMember(
                "OpenView",
                BindingFlags.InvokeMethod,
                binder: null,
                target: databaseCom,
                args: new object[] { query });

            if (viewCom is null)
            {
                return null;
            }

            viewCom.GetType().InvokeMember(
                "Execute",
                BindingFlags.InvokeMethod,
                binder: null,
                target: viewCom,
                args: null);

            recordCom = viewCom.GetType().InvokeMember(
                "Fetch",
                BindingFlags.InvokeMethod,
                binder: null,
                target: viewCom,
                args: null);

            if (recordCom is null)
            {
                return null;
            }

            var value = recordCom.GetType().InvokeMember(
                "StringData",
                BindingFlags.GetProperty,
                binder: null,
                target: recordCom,
                args: new object[] { 1 });

            return value?.ToString();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (viewCom is not null)
            {
                try
                {
                    viewCom.GetType().InvokeMember(
                        "Close",
                        BindingFlags.InvokeMethod,
                        binder: null,
                        target: viewCom,
                        args: null);
                }
                catch
                {
                    // Ignore close failures.
                }
            }

            ReleaseComObject(recordCom);
            ReleaseComObject(viewCom);
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}
