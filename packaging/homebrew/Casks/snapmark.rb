cask "snapmark" do
  arch arm: "arm64", intel: "x64"

  version "1.8.0"
  sha256 arm:   "0a8ea1c6bca81c98a5561b073fa07f34e22dc073dbb48b80501501d90152db5f",
         intel: "4ba76dff713fb61d9d2c93d86be5c914e55d1d285ea77f78fa7e0b19dd7d2e1c"

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
