# The server half of a Tatooine deploy. Runs ON the box, as root, over SSM.
#
# Its caller (tatooine-deploy.yml) prepends `set -eu` and the four variables below, so this file is a fragment rather
# than a standalone script: BUCKET, KEY, COMMIT and GATE arrive already quoted with printf %q. That is the whole reason
# it is a file at all — the previous version lived in an unquoted heredoc inside the workflow and relied on escaping
# every `\$` by hand, which is a puzzle that only has to be got wrong once to produce a broken command on a live box.
#
# The order is load-bearing:
#
#   1. stage the new release beside the old one, touching nothing that is running;
#   2. stop the service, so the world stops changing;
#   3. (gate) copy the now-quiescent world and open the COPY with the new build;
#   4. only then swap the symlink and start.
#
# Step 3 after step 2 is the correctness requirement. Copying a world while the server writes to it gives a set of
# files captured at different instants — not even crash-consistent — so the gate could fail, or pass, for reasons that
# had nothing to do with the schema.

release="/opt/tatooine/releases/$COMMIT"
rm -rf "$release"
mkdir -p "$release"

aws s3 cp "s3://$BUCKET/$KEY/server.tar.gz" /tmp/server.tar.gz --quiet
tar -xzf /tmp/server.tar.gz -C "$release" --strip-components=1
chmod +x "$release/SwgTatooine.Server" "$release/run.sh"
chown -R tatooine:tatooine "$release"
echo "staged $COMMIT"

data=$(sed -n 's/^DB_DIR=//p' /etc/tatooine/server.env)

if [ "$GATE" = "true" ] && [ -n "$data" ] && [ -d "$data" ]; then
    # Stop first: see the header. The service is going to be restarted by this deploy anyway, so the gate costs no
    # downtime that the deploy was not already spending.
    systemctl stop tatooine.service || true

    gate=/tmp/gate
    rm -rf "$gate"
    mkdir -p "$gate"

    # Space is measured on the filesystem the copy is WRITTEN to, not the one it is read from. Measuring the source --
    # prod's 200 GB data volume -- while copying to /tmp on a 40 GB root is how a gate fills the root disk and takes the
    # box down to check a deploy, which is the exact opposite of what it is for.
    need=$(du -sk "$data" | cut -f1)
    free=$(df -Pk "$gate" | awk 'NR==2 {print $4}')
    if [ "$free" -le "$((need + 1048576))" ]; then
        echo "not enough free space on $(df -P "$gate" | awk 'NR==2 {print $6}') to copy the world: ${need} KB needed, ${free} KB free" >&2
        systemctl start tatooine.service
        exit 1
    fi

    mkdir -p "$gate/world"
    cp -a "$data/." "$gate/world/"

    # The new binary, the OLD world. Two hundred ticks is past the reopen and into steady state, which is where a schema
    # mismatch surfaces: an archetype that cannot be opened throws on first access, not at the file header.
    if ! "$release/SwgTatooine.Server" --persist --db-dir "$gate/world" --ticks 200 --warm 0 --hz 10; then
        echo "SCHEMA GATE FAILED: the new build could not open the existing world; the live one is untouched" >&2
        rm -rf "$gate"

        # Back up on the OLD release. A failed gate must leave the box serving what it was serving.
        systemctl start tatooine.service
        exit 1
    fi

    rm -rf "$gate"
    echo "SCHEMA GATE PASSED: the new build opened the existing world"
fi

# -n so the swap never descends into an existing symlink's target, -f so it replaces, -T so it is treated as a file and
# not a directory to put a link inside. Without -T this creates current/current on the second deploy and the service
# keeps running the first build while the logs say otherwise.
ln -sfnT "$release" /opt/tatooine/current

systemctl restart tatooine.service
sleep 5
if ! systemctl is-active --quiet tatooine.service; then
    journalctl -u tatooine.service -n 40 --no-pager
    exit 1
fi

echo "deployed $COMMIT; releases on the box: $(ls /opt/tatooine/releases | tr '\n' ' ')"
