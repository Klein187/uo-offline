# Putting this on GitHub

Step-by-step guide for pushing this project to your GitHub account from
the Steam Deck (or any Linux machine).

This is a one-time setup. Future updates are just `git add`, `git commit`,
`git push`.

---

## 1. Pick a repo name

Some good ones:

- `uo-offline`              ← short, clear
- `uo-offline-deck`         ← emphasizes Steam Deck
- `uo-modernuo-installer`   ← descriptive, less catchy
- `britannia-offline`       ← thematic

The README and zip filename are already "uo-modernuo", but you can
rename the GitHub repo to whatever you want — the contents don't care.

I'll use `uo-offline` in the examples below. Substitute your choice.

---

## 2. Create the repo on GitHub

In a web browser:

1. Go to https://github.com/new
2. **Repository name:** `uo-offline` (or your pick)
3. **Description:** "One-command installer for offline single-player Ultima Online on Linux and Steam Deck"
4. **Public** (so others can find it).
5. **Do NOT** check "Add a README" / "Add .gitignore" / "Choose a license" —
   we already have those in the project folder. Adding them on GitHub
   causes a merge conflict on first push.
6. Click **Create repository**.

GitHub will show a page with setup instructions. Copy the URL it shows,
which looks like:

    https://github.com/YOUR-USERNAME/uo-offline.git

---

## 3. One-time git config on the Deck

Tell git who you are. Use the email associated with your GitHub account.

```
git config --global user.name "Your Name"
```

```
git config --global user.email "you@example.com"
```

---

## 4. Authentication (Personal Access Token)

GitHub doesn't accept account passwords for pushing code anymore.
You need a Personal Access Token (PAT) instead.

1. Go to https://github.com/settings/tokens
2. **Generate new token** → **Generate new token (classic)**
3. **Note:** "Steam Deck push" (or any label you'll recognize)
4. **Expiration:** 90 days is fine for now, or "No expiration" if you
   trust this Deck and don't want to deal with renewal.
5. **Select scopes:** check `repo` (the top-level checkbox).
6. **Generate token** at the bottom.
7. Copy the token immediately. It only shows once. Stash it in a
   password manager or somewhere safe.

When git asks for a password during push, paste this token instead.

---

## 5. Initialize the repo and push

From the folder containing `install.sh`, `README.md`, etc:

```
cd ~/Downloads/uo-modernuo
```

Initialize git in this folder:

```
git init -b main
```

Add all the files:

```
git add .
```

Commit them:

```
git commit -m "Initial release"
```

Connect to your GitHub repo (replace YOUR-USERNAME):

```
git remote add origin https://github.com/YOUR-USERNAME/uo-offline.git
```

Push:

```
git push -u origin main
```

It'll ask for **Username** (your GitHub username) and **Password**
(paste the Personal Access Token from step 4 — characters won't show
as you paste, that's normal). Hit Enter.

If it succeeds, refresh your repo page on GitHub. All your files will
be there.

---

## 6. Polish the repo page

Things worth doing once it's up:

**Add a description and topics.** Click the gear icon next to "About"
on your repo's main page. Add:

- Description: one-liner, same as you used when creating the repo
- Topics: `ultima-online`, `uo`, `modernuo`, `classicuo`, `steam-deck`,
  `linux`, `installer`, `mmo`, `offline-game`
- Website: leave empty unless you have one

Topics help people find the repo via GitHub search.

**Pin the repo** to your profile if you're proud of it. Profile page →
"Customize your pins" → check this repo.

**Add screenshots.** GitHub renders images inline in the README.
Create a `docs/` folder, drop your Britannia screenshots in there,
and add to the README:

    ## Screenshots
    ![Standing in Magincia](docs/screenshot-magincia.png)

**Releases.** When you make changes worth bundling, go to **Releases**
→ **Create a new release**. Tag it `v1.0.0`. Upload a `uo-modernuo.zip`
built from the latest source. Users can then download a known-good
snapshot instead of grabbing whatever's on `main`.

---

## 7. Making changes later

After the first push, the workflow is shorter:

```
cd ~/Downloads/uo-modernuo
```

```
git add .
```

```
git commit -m "describe what you changed"
```

```
git push
```

GitHub remembers your token from the first push (KDE has a credential
helper). If it asks again, paste the same token.

---

## 8. If you ever want to make the install easier for users

Right now users have to download the zip from GitHub Releases.
A nice future improvement: a one-liner that does everything.

```
curl -fsSL https://raw.githubusercontent.com/YOUR-USERNAME/uo-offline/main/install.sh | bash
```

This works *almost* directly, but `install.sh` currently assumes the
`scripts/` folder is next to it. To enable the one-liner install, the
script would need to be tweaked to fetch its companion scripts from
GitHub if they're not adjacent locally. That's a future polish.

---

## Troubleshooting

**`git push` says "Authentication failed".** The token was wrong or
expired. Generate a new one at https://github.com/settings/tokens and
re-paste.

**`git push` says "Updates were rejected because the remote contains
work that you do not have locally".** You probably checked "Add a
README" or similar when creating the repo on GitHub. Pull first:

```
git pull origin main --allow-unrelated-histories
```

Resolve any conflicts in the editor, then re-push.

**You want to start over.** From the project folder:

```
rm -rf .git
```

Then re-do steps 5 onward.
