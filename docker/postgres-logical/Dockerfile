# PostgreSQL with the wal2json output plugin, for phase 34's logical-decoding reader.
#
# A build step rather than an image swap, because there is no longer a well-known public image that
# carries wal2json. Debezium's postgres images are the usual answer and stopped being one: as of
# Debezium 3.x their Dockerfile builds `decoderbufs` and nothing else. Checked rather than assumed.
#
# Debian rather than the -alpine base the compose file used before this: the official postgres image
# already has the PGDG apt repository configured, and PGDG publishes wal2json as a package — so this
# is one `apt-get install` with no compiler, no headers and no source checkout, which is a great deal
# less to go wrong than building an extension from source against an Alpine base whose PostgreSQL
# headers come from a different package set than its server does.
FROM postgres:17

RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-17-wal2json \
    && rm -rf /var/lib/apt/lists/*
