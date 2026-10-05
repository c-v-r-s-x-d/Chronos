# Chronos — operations

Details for administrators: what the service changes on a machine, how it puts things back, and what to do when it is not working. For what Chronos is and how to use it, see the [README](../README.md).

## The DNS resolver

For the duration of a session, every active network interface that has a DNS server of its own is
pointed at the resolver: its server list becomes `127.0.0.1, <its own first server>`. Queries for
anything not on the block list are forwarded to that original server and answered normally; queries
for a blocked domain get `NXDOMAIN`. The original server stays second on purpose — if the resolver
ever stops without a clean shutdown, the interface still has a working server to fall back to.

An interface with no server of its own outside this machine is left alone. Putting `127.0.0.1`
there with nothing behind it would leave the interface unable to resolve anything the moment the
resolver stops, and that is worse than not blocking sites on it.

A DHCP interface is turned into a **static** two-entry list for the session, not left on DHCP: there
is no way to add a second server to a DHCP interface and keep it on DHCP. The backup still says
DHCP, so the end of the session puts it back on DHCP. If the network changes mid-session, each pass
reads the server DHCP now hands the interface and moves the second entry to it.

The takeover sets the original server first and then puts `127.0.0.1` in front of it, so a `netsh`
step that fails leaves the interface on its own server, never on `127.0.0.1` alone.

**The backup.** Before an interface is touched for the first time, its original settings are saved
in two independent places: a file, `%ProgramData%\Chronos\dns-backup.json`, and a string value
named `DnsBackup` under the registry key `HKLM\SOFTWARE\Chronos`. Either one alone restores the
machine; losing one does not lose the other. A restore reads whichever copy is newer, or the only
one that is usable. Afterwards the backup keeps only what is still left to put back, and both copies
are cleared once nothing is. A session saves its full backup again before it touches anything.

**Putting it back.** A session ending restores every interface in the backup exactly as it found it
— DHCP back to DHCP, a static list in the same order. Stopping the service does not (see
[Known limitations](#known-limitations)); `chronos recover`, `chronos clean` and `chronos uninstall`
read the backup and call `netsh` directly, without the service.

- An interface that refused is kept in the backup; an idle service retries it every pass and reports
  the layer as failed until it goes through.
- An interface no longer on the machine (unplugged, disabled) stays in the backup without failing
  the rest. When it returns, the service, the next session's end or one of the commands above puts
  it back; after uninstall, only the manual fallback below does.

`chronos diag` reports on all of this under `[dns-l2]` without changing anything — see
[`chronos diag`](#chronos-diag).

**Manual fallback**, if none of the above is available. For an interface that was on DHCP:

```
netsh interface ipv4 set dnsservers name=<index> source=dhcp
```

For one that had a static list, set the first server and add the rest at their position:

```
netsh interface ipv4 set dnsservers name=<index> source=static address=<first server> validate=no
netsh interface ipv4 add dnsservers name=<index> address=<next server> index=2 validate=no
```

`<index>` is the interface's numeric index (`Get-NetAdapter` lists it), not its name — `netsh` needs
the index because an adapter's name is written in whatever language Windows was installed in.

Only the DNS server list is ever touched. Registration of the interface's name in DNS, the DNS
suffix, and IPv6 resolvers are read from nowhere and written to nowhere by this layer — see
[Known limitations](#known-limitations).

## Installing

### From the MSI

```
msiexec /i Chronos.msi
```

Windows asks for elevation. Refuse it and the installation stops with a sentence saying why,
rather than an access error part-way through writing to `Program Files`. Add `/qn` for an
installation with no interface at all.

An interactive installation opens the tray app when it finishes, as the installing user and
without administrator rights. A silent one (`/qb`, `/qn`) does not; the tray app then starts at the
next login. Installing a package of the same version replaces the installed one.

The package lays the files into `C:\Program Files\Chronos`, puts **Chronos** shortcuts on the
desktop and in the Start Menu, and then calls `chronos install` out of the folder it just filled.
That command is what actually changes the machine:

- creates `%ProgramData%\Chronos`, writable by SYSTEM and Administrators and readable by Users;
- registers the `Chronos` event log source in the **Application** log;
- registers the `ChronosService` service — automatic start, LocalSystem, depending on `Tcpip` and
  `Dnscache`, and told to restart itself 5 seconds after a failure, then 10, then every minute;
- registers the `Chronos\Recovery` scheduled task, which runs `chronos recover` as SYSTEM at every
  boot;
- starts the service.

The package also adds the interface to the autostart of **the account that installs it**, and owns
that entry: Windows writes it during the installation and takes it away again during the removal,
which is something no part of an installation running as the system could do for anybody. Every
other account gets its own the first time that person runs the interface, which is the only way an
entry can reach a hive an installer cannot see — and the entries of those accounts are the
limitation described under [Known limitations](#known-limitations).

### From a build

The same registration, without the MSI. Publish all three applications into one directory — the
service, the command line and the interface belong together, and `chronos install` registers the
`Chronos.Service.exe` that sits beside it:

```
dotnet publish src/Chronos.Service/Chronos.Service.csproj -c Release -r win-x64 --self-contained -o C:\Chronos
dotnet publish src/Chronos.Cli/Chronos.Cli.csproj     -c Release -r win-x64 --self-contained -o C:\Chronos
dotnet publish src/Chronos.App/Chronos.App.csproj     -c Release -r win-x64 --self-contained -o C:\Chronos

C:\Chronos\chronos.exe install
```

Publish rather than copy out of `bin`: publish is the build output that puts
`System.Diagnostics.EventLog.Messages.dll` beside the programs, and that file is what lets Event
Viewer render a Chronos entry as text instead of "The description for Event ID … cannot
be found".

`chronos install` can be run on a machine that already has the service: it does not remove
anything and does not stop a running session, it brings the registered service in line with the
build it was run from — the executable it points at, its dependencies, its description and the
restarts it gets after a failure — and says so. That is what makes installing from a build and
then running the MSI work: the MSI reads a refusal as "roll this installation back", so this
command has no refusal to give it.

## Removing it, and what stays

```
msiexec /x Chronos.msi                 keeps your lists and logs
msiexec /x Chronos.msi REMOVEDATA=1    removes them too
```

Or, on a machine where the MSI is gone and its traces are not:

```
chronos uninstall
chronos uninstall --purge
```

Either way the removal takes off, in this order: the hosts entries, every Chronos object in the
filtering platform and the DNS settings of every interface the resolver had taken over, then the
service — restoring the DNS settings once more, in case a last pass of the still-running service
took the interfaces again in between — then `HKLM\SOFTWARE\Chronos` if nothing is left in it, the
saved session, the scheduled task and its folder, the autostart entry and the event log source. It carries on past a step it could not finish and
says which one at the end, because a machine where the product is gone and its changes are not is
the state you have no tool left to get out of.

The autostart entry is the one step the two routes do differently. `chronos uninstall` removes the
entry of the account running it, and no other — a command runs in one user's registry and cannot
reach another's. Under the MSI it removes nobody's, because the installer runs it as the system,
where there is no user hive to look in; the entry the package wrote is removed by Windows instead,
out of the hive of whoever runs the removal, which is the account that installed unless somebody
else removes it. What neither route reaches is every other account's — see
[Known limitations](#known-limitations).

The MSI closes `Chronos.App.exe` itself, at the very start of an installation, upgrade or removal,
so Windows never asks you to close it. A running executable cannot be replaced or deleted, and the
interface is running on any machine it was used on. It is ended outright rather than asked to
close, because closing its window only sends it to the tray. Nothing is lost: the session lives in
the service's `state.json`. An interactive installation opens the interface again when it finishes;
after a silent one it starts at the next login.

**What survives an ordinary removal:** `%ProgramData%\Chronos` — your lists, your settings and the
service logs. Install again and they are where you left them. The saved session is not among them:
it goes on every removal, so that reinstalling does not resume a session you removed the product to
end. `dns-backup.json` lives in the same directory, but by the time removal finishes there should
be nothing left in it: the DNS settings are restored, and a completed restore clears both copies of
the backup along with them. An interface that refused or is not on the machine keeps its copy, and
with it the registry key.

**What survives even `--purge`:** `%LOCALAPPDATA%\Chronos`, where the interface keeps its own logs
for each user; and the autostart entries of every other account on the machine — see below.

**Upgrading removes the session.** Installing a newer package over an older one runs the old
package's `chronos uninstall` before it lays the new files down, and that clears `state.json` like
any other removal. So an upgrade ends a session that was running, cool-down and all. It is the
right end of the trade — the alternative is a removal that leaves a session to come back after a
reinstallation somebody performed to end it — but it is worth knowing before you upgrade during
one.

## When the service is not working

Three commands, all of them administrator-only, none of them needing a service that answers.

### `chronos recover`

For a machine that booted into a broken installation. The `Chronos\Recovery` task runs it at every
boot; you can also run it by hand.

It first asks whether anything is actually wrong: it waits up to two minutes for a service that is
both running and answering, nudging a stopped one into starting once. If one turns up, **it changes
nothing** and says so. If none does, it takes the whole block off — hosts entries, filters,
provider, sub-layer, the DNS settings of every interface the resolver had taken over — clears the
saved session however much of it was left to run, and writes one `Error` entry to the Application
log. That entry is the only record, because a task that runs at every boot and reports success
every time is a task whose entries get filtered out.

### `chronos clean`

Takes every change Chronos makes to a machine back off it, immediately and without asking whether
the service is healthy. It exists for the service that will not start while its changes are still
applied. It does not stop the service, so on a machine whose service is running with a live
session the block — and the DNS settings — will be back on the next pass: stop the service first,
or use `recover`, which decides for you.

`clean` is also the only thing that removes the persistent filtering-platform provider and
sub-layer left behind by early development builds.

### `chronos diag`

Writes one archive describing the state of the machine and prints its path — into
`%ProgramData%\Chronos\diag`, or into `%TEMP%` when there is no data directory, which is one of
the shapes "this machine is broken" takes. It changes nothing.

The report inside has a section each for versions, the product's files, its logs, the filters in
force, the hosts block, who owns port 53, the DNS settings of the active interfaces, the resolver
layer, the DNS backup, the service, the recovery task, the layers, the rules, and the interface's
event source. Every one of those may say that it could not be read, and that is itself a finding.
`--verbose` turns counts into contents: the addresses, the hosts block itself, the rules.

The resolver layer's own section, `[dns-l2]`, says whether `127.0.0.1:53` is free or who holds it,
whether the backup is in the file, the registry, or both (when each copy was saved and how many
interfaces it names; the interfaces themselves for the copy a restore would use), and which
active interfaces currently point at `127.0.0.1`. No domain name appears in it — this reports on
the machine, not on where anyone went.

## The command line

```
chronos <status|start|extend|unlock|cancel|watch|clean|recover|diag|install|uninstall>
        [--minutes N] [--purge] [--verbose]
```

| Exit code | Meaning |
|---|---|
| 0 | Done |
| 1 | The command was rejected — an unlock still on its cool-down, say |
| 2 | The command line is wrong, or it was not run as an administrator |
| 3 | The service could not be reached |
| 4 | Something on this machine could not be changed |

`chronos install` answers 0 when everything registered, even if taking off an older build's
leftovers failed first, and even on a machine that already had the service: that is a warning,
printed and written to the event log, and not a reason for an MSI to roll a good installation back.
The MSI reads that answer as zero against non-zero and nothing finer — any non-zero code is error
1722 and a rollback — which is why `install` has no rejection of its own to give. `chronos
uninstall` answers 4 when any step failed, and the package deliberately ignores that code — see the
comments in `src/Chronos.Installer`.

Everything the command line and the event log say is in English, whatever the machine's language.
Only the interface is translated.

## Known limitations

**Autostart entries belonging to other users are removed by nothing.** The package writes its entry
into the hive of the account that installs and Windows removes it out of the hive of the account
that uninstalls; `chronos uninstall` removes the entry of the account that runs it. Every other
account's is out of reach of both: the interface
creates its own entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` the first time
each person runs it, and neither an installer running as the system nor a command running in one
account's registry can see another's hive. After the removal the executable those entries name is
gone, so the interface never starts for those accounts again and never gets the chance to tidy up
after itself. **Those entries stay until somebody deletes them by hand.** The uninstall says so on
the way out.

**The filtering-platform provider and sub-layer exist only while something is blocked.** The
service creates them with the first filter and removes them with the last, so a machine that is
blocking nothing shows no Chronos objects in `netsh wfp show state`. Early development builds
created them at installation and left them behind; `chronos clean` (and so `install`, `uninstall`
and `recover`) removes those.

**The interface's own logs stay in each user's profile.** `Chronos.App` writes them to
`%LOCALAPPDATA%\Chronos\logs`, one copy per account. `REMOVEDATA=1` takes away
`%ProgramData%\Chronos`, which is everything the service owns, but the removal runs as the system
and cannot reach a user profile any more than it can reach a user's autostart entry. They are
plain text, they contain no paths to anything you visited, and they are yours to delete.

**Stopping the service does not put the DNS settings back by itself.** `net stop`, a crash, or a
reboot leaves the active interfaces pointed at `127.0.0.1, <original server>` with nothing left
listening on `127.0.0.1` — names still resolve, because Windows falls back to the second server, but
the settings stay that way until the service starts again (with no session it puts them back at
once; with one, when it ends), or you run
`chronos recover`, `chronos clean` or `chronos uninstall`. The `Chronos\Recovery` scheduled task
runs `recover` at every boot for exactly this reason.

**Port 53 taken by another resolver leaves the DNS layer unavailable.** The resolver needs
`127.0.0.1:53`, UDP and TCP both; if something else already holds either one, the layer does not
come up, the interface says why, and the DNS settings of your interfaces are left untouched. Sites
and applications still block through the other two layers.

**DNS resolved inside a VPN tunnel is not covered.** This layer reconfigures ordinary network
interfaces through `netsh`. A VPN client that resolves names itself — its own virtual adapter, or a
split-tunnel policy that sends some names through the tunnel — can answer a query without it ever
reaching `127.0.0.1`.

**A VPN that filters DNS to `127.0.0.1` leaves the DNS layer unavailable, and it says so.** Some
VPN clients (AmneziaVPN and other WireGuard-based ones, for example) install firewall rules that
drop every DNS packet not sent to the tunnel's own server, including ones to `127.0.0.1:53`. Before
it touches any interface, and on every pass while it holds them, the layer sends a query to its own
resolver, waiting a second for the answer; while it holds interfaces it asks twice before giving
up. If nothing comes back, it takes nothing (or gives back what it held, as at the end of a
session), the interface shows the DNS layer as unavailable with the reason, and
sites still block through the other two layers. It asks again once a minute, so turning the VPN off
brings the layer back within a minute.

**IPv6 resolvers are not touched.** The resolver listens on `127.0.0.1` only, and only an
interface's IPv4 server list is read, backed up or changed. A real, reachable IPv6 resolver
bypasses this layer entirely; Windows' own IPv6 defaults (`fec0:0:0:ffff::1..3`) are not reachable
on an ordinary network, so in practice this rarely matters.

**A blocked-site notice names the entry from your list, not the exact address that was asked for.**
A page load can resolve dozens of subdomains of one blocked site; the notice — and the five-minute
limit on how often it repeats — is keyed to the domain you blocked, not to each one of them.

**The .NET runtime inside Chronos is updated only by a new Chronos release.** The package carries
its own runtime, so nothing has to be installed first — and for the same reason Windows Update does
not patch it. A security fix in .NET reaches Chronos when Chronos is rebuilt and reinstalled.

**A failed installation does not promise a clean machine.** `chronos install` stops at the first
step it could not do and tries to undo what it had done, but that rollback is a best effort: if
removing a half-registered service is itself refused, the service stays and you are told only
about the original failure. After an installation that failed, check the machine rather than
trusting the rollback — `chronos diag` is the quickest way to see all of it at once.

## Where things live

| Path | What |
|---|---|
| `C:\Program Files\Chronos` | The product |
| `%ProgramData%\Chronos\config.json` | Your lists and settings |
| `%ProgramData%\Chronos\state.json` | The running session |
| `%ProgramData%\Chronos\dns-backup.json` | One of the two copies of the original DNS settings, while a session holds any |
| `%ProgramData%\Chronos\logs` | Service logs |
| `%ProgramData%\Chronos\diag` | Diagnostic archives |
| `%LOCALAPPDATA%\Chronos\logs` | Interface logs, per user |
| `HKLM\SOFTWARE\Chronos`, value `DnsBackup` | The other copy of the original DNS settings |
| Application event log, source `Chronos` | Installation, removal, recovery, and failures the service could not log anywhere else |
