# Security

## Reporting a problem

Please don't open a public issue for a security problem. Use GitHub's
[private vulnerability report](https://github.com/RocketMyrr/WindowsGSM-Next/security/advisories/new) instead,
with what you found and how to reproduce it. You'll get an answer, and a fix before details are made public.

## How WindowsGSM protects your machine

**Who can do what**
- Everyone signs in with their own account: owner, admin, operator, viewer or member. Members get only the
  servers and permissions you grant. Every action is in the audit log with who did it and from where.
- **Files** and **Add-ons** permissions let someone change the programs a server runs on your PC. Give them only
  to people you'd trust with the PC itself. Server scripts and plugins are limited to admins and owners.

**Signing in**
- Passwords are stored as PBKDF2-SHA256 hashes (600,000 iterations); older hashes upgrade at the next sign-in.
- Two-factor sign-in, with replayed codes refused, and passkeys.
- Wrong passwords lock that account for 5 minutes after 5 tries, and each address is rate-limited. A wrong
  username takes as long to answer as a wrong password, so usernames can't be found by timing.
- Sessions can be listed and signed out from Account & security. Changing the password signs out everything else.
- The WindowsGSM app on another PC stays signed in with its own key. Only a hash of it is kept, and it's
  listed in Account & security, where it can be removed.

**On the network**
- Off by default: the panel listens only on this PC until "Reachable from other computers" is turned on.
- HTTPS with your own certificate, Let's Encrypt or a self-signed one. The app on another PC pins a self-signed
  certificate by fingerprint and stops if it changes.
- Strict content security policy, CSRF protection on every change, `SameSite=Strict` cookies and no framing.
- Machines joined to a hub use their own credential. A request relayed by the hub runs as the hub user, and the
  machine's own permission checks decide what's allowed.
- First-run setup from another computer needs the one-time code in the agent's log. The code is replaced after
  10 wrong guesses.

**Files and downloads**
- The file manager can't leave a server's folder: `..` paths and links that point outside are refused.
- Archives (backups, add-ons, imports) are unpacked safely: entries that would land outside are skipped.
- Custom add-on downloads can't reach this PC itself or link-local addresses such as cloud metadata at
  169.254.169.254, including after redirects.

**Your data**
- Secrets (Steam password, Discord bot token, webhook URLs, certificate and storage passwords, the hub link and
  sign-in cookie keys) are encrypted with Windows data protection for your Windows account.
- Settings files are written atomically, with the previous copy kept. A damaged file is never silently replaced
  by an empty one: it's kept aside and shown in Health checks.
- Nothing is sent anywhere you haven't set up.
