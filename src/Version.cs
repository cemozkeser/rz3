using System;
using System.Reflection;

namespace Rz3
{
    // ---------------------------------------------------------------------------------------
    // ONE place that defines the version of this program.
    //
    // A release changes exactly this file plus a git tag "v<same number>", and the build
    // script refuses to produce a release artifact when the two disagree (see
    // scripts/build-release.ps1). The installer reads the same number, so the window title,
    // the exe's file version, the installed folder (Programs\Rz3\1.0.0), the Start-menu entry
    // and the Add/Remove Programs row can never drift apart.
    //
    // Versioning policy for this project:
    //   MAJOR - a change that removes or breaks something users can feel: a protocol meaning
    //           changes, a state file stops being read, a supported mouse or Windows family is
    //           dropped, the install layout moves again.
    //   MINOR - new capability, no break: a new tab, a new output type, a new host action kind,
    //           support for another Razer HID product.
    //   PATCH - fixes only: bug fixes, test additions, wording, documentation.
    // The first version that is meant to be used by other people is 1.0.0.
    // ---------------------------------------------------------------------------------------
    public static class VersionInfo
    {
        public const string Version = "1.0.0";

        // Read from the assembly attributes the build script injects (/version:1.0.0.0). Used
        // only to prove at run time that the binary and this source file agree; the constant
        // above stays authoritative so a hand build without the flag still reports a version.
        public static string AssemblyVersion
        {
            get
            {
                try
                {
                    Version v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v == null ? "" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
                }
                catch { return ""; }
            }
        }

        // "Rz3 - Basilisk V3 Pro 35K (no Synapse)" used to be the title; the version now rides
        // in it so a screenshot in a bug report identifies the build by itself.
        public static string Title { get { return "Rz3 " + Version + " - Basilisk V3 Pro 35K (no Synapse)"; } }

        public static string Short { get { return "Rz3 " + Version; } }
    }
}
