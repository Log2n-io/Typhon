# The bot-box half of a Tatooine deploy. Runs ON the box, as root, over SSM.
#
# Its caller prepends `set -eu` plus BUCKET, KEY and COMMIT, already quoted with printf %q — see
# tatooine-deploy-server.sh's header for why this is a file and not a heredoc.

release="/opt/tatooine/releases/$COMMIT"
rm -rf "$release"
mkdir -p "$release"

aws s3 cp "s3://$BUCKET/$KEY/bots.tar.gz" /tmp/bots.tar.gz --quiet
tar -xzf /tmp/bots.tar.gz -C "$release" --strip-components=1
chmod +x "$release/SwgTatooine.Bots" "$release/SwgTatooine.BotConsole" "$release/run-fleet.sh" "$release/run-console.sh"
chown -R tatooine:tatooine "$release"

ln -sfnT "$release" /opt/tatooine/current

# The console restarts; the fleet does NOT. A fleet run is a job with an end (Type=oneshot), and restarting it from a
# deploy would open a thousand sessions nobody asked for against a server that may be mid-deploy itself.
systemctl restart tatooine-botconsole.service
sleep 3
if ! systemctl is-active --quiet tatooine-botconsole.service; then
    journalctl -u tatooine-botconsole.service -n 40 --no-pager
    exit 1
fi

echo "deployed $COMMIT to the bot box"
