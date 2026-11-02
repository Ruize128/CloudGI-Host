# Annex child-session integration

Upstream: https://github.com/mattxslv/annex
Pinned revision: `8a98c1bd1440b8712c5b659de49477b7e9f821d1`
License: MIT (see LICENSE).

`Annex.Core.cs` is included unchanged and compiled into CloudGI.Host.Session.
The ActiveX host and connection sequence in SessionWindow.cs are adapted from
upstream Annex.cs and LauncherApplication.cs under the same license.

CloudGI uses its existing bundled Sunshine, automatic pairing and game control;
it does not run the Annex installer, download another Sunshine, install a Startup
hook, change Windows Hello/UAC, or patch Terminal Services. Windows credentials
are held in memory for this experiment and are never logged or persisted.
The viewer must remain attached; minimizing it keeps the child desktop alive.
