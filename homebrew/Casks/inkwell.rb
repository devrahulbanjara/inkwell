# Put this file in a separate repo named `homebrew-inkwell` (path: Casks/inkwell.rb).
# Users then run:  brew tap devrahulbanjara/inkwell && brew trust devrahulbanjara/inkwell && brew install --cask inkwell
# Update `version` and `sha256` (printed by scripts/make-dmg.sh / the release notes) on each release.
cask "inkwell" do
  version "0.1.0"
  sha256 "REPLACE_WITH_DMG_SHA256"

  url "https://github.com/devrahulbanjara/inkwell/releases/download/v#{version}/Inkwell-#{version}.dmg"
  name "Inkwell"
  desc "AI text triggers (?fix, ?ask, ?rewrite…) in any app, from the menu bar"
  homepage "https://github.com/devrahulbanjara/inkwell"

  depends_on macos: ">= :sonoma"

  app "Inkwell.app"

  caveats <<~EOS
    Inkwell is ad-hoc signed (not notarized). On first launch macOS may block it:
      System Settings → Privacy & Security → "Open Anyway".
    Then allow Inkwell under Privacy & Security → Accessibility.
  EOS

  zap trash: [
    "~/Library/Application Support/Inkwell",
    "~/Library/Preferences/io.github.inkwell.plist",
  ]
end
