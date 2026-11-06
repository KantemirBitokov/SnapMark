cask "snapmark" do
  arch arm: "arm64", intel: "x64"

  version "1.9.3"
  sha256 arm:   "5562a4968429f0f1c07c8f15cf974f8d5edbe4eda638f8a8f9f19b1dcc4b2bc6",
         intel: "1b4c095a8089522a368aa7e0a8532874f7fc150ce25e0bdfe3fa2b88165fdfdb"

  url "https://github.com/KantemirBitokov/SnapMark/releases/download/v#{version}/SnapMark-#{version}-macOS-#{arch}.dmg"
  name "SnapMark"
  desc "Screenshots with A-B-C markers and comments, screen recording with sound"
  homepage "https://github.com/KantemirBitokov/SnapMark"

  depends_on macos: ">= :big_sur"

  app "SnapMark.app"

  postflight do
    # Программа не нотаризована Apple — снимаем карантин, чтобы она запускалась без «повреждено».
    system_command "/usr/bin/xattr", args: ["-cr", "#{appdir}/SnapMark.app"]
  end

  zap trash: [
    "~/.config/SnapMark",
    "~/Library/LaunchAgents/pro.sadlybear.snapmark.plist",
  ]

  caveats <<~EOS
    For video recording install ffmpeg:  brew install ffmpeg
    On first launch allow Screen Recording in System Settings → Privacy & Security.
  EOS
end
