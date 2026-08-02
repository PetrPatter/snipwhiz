# GitHub Releases as the update feed

Snipwhiz updates itself from unauthenticated GitHub Releases on its own public
repository, hard-coded as a `const` in `Updater.cs`. It was chosen because it is
the only option that costs nothing to run and embeds no credential: the releases
are public, so no token is needed, and unauthenticated requests are rate-limited
per IP at 60 an hour against an app that asks once per launch.

## Consequences

**Publishing 1.0.0 is what makes this irreversible.** The feed URL is compiled
into every installed copy, and the update mechanism is the only thing that could
replace it — so it cannot replace itself with a different address. Moving the
feed does not migrate existing installs; it strands them on whatever version they
were running, permanently and silently, because every failure here is silent by
design. The recovery is asking people to download and run a new installer by hand.

That is acceptable at this size and would stop being acceptable at a size where
you no longer know who is running it.

**No control over the host.** No CDN, no analytics, no ability to serve a
different build to a subset of users, and an outage or a policy change at GitHub
is an outage of the update path. All of which are things this project does not
want and would have to build if it hosted the feed itself.
