cask "snapmark" do
  arch arm: "arm64", intel: "x64"

  version "1.9.6"
  sha256 arm:   "f5e69fb86f78c3f046e9de8d66de00748ce8f8fe79c32eea3b828a2c0119607b",
         intel: "53b9ba2913b7a7d02a250c742c4eaaf26144c96a53d6aa3394eee19f0228725a"

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
