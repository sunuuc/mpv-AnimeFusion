# Bangumi authorization

ASP.NET Core OAuth service for mpv-AnimeFusion. App Secret stays on the server. Users authorize in their browser; the player receives tokens through a single-use handoff protected by SHA-256 proof.

## Deploy

Copy this directory to `/opt/mpv-animeve-auth`. Create `deploy/secrets/app-id` and `deploy/secrets/app-secret` with the project's Bangumi credentials. Keep the secrets directory accessible only to its owner; the mounted files must be readable by the container user.

Register this callback in the Bangumi application:

```text
https://bangumi.sunuuc.de5.net/oauth/callback
```

Enable collection READ and WRITE. The player does not request directory, forum or wiki write permissions.

Prepare `deploy/acme`, `deploy/certificates` and `deploy/data`; give `deploy/data` to UID 1654. Obtain the domain certificate using Certbot's webroot at `/var/www/acme` before starting the HTTPS proxy.

```sh
cd /opt/mpv-animeve-auth/deploy
docker compose up -d --build
cp animeve-auth-renew.service animeve-auth-renew.timer /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now animeve-auth-renew.timer
```

`/healthz` reports process health. `/readyz` checks whether application credentials are configured. The callback redirects to the player's fixed local listener at `http://127.0.0.1:33165/`; it carries an expiring handoff code rather than tokens. Request access logs are disabled. Authorization credentials and tokens are never written to application logs.

The service uses the MIT-licensed ASP.NET Core OAuth handler. Nginx and Certbot retain their licenses in their official container images.
