cask "snapmark" do
  arch arm: "arm64", intel: "x64"

  version "1.9.19"
  sha256 arm:   "7b86a65455cf7cb356b73adb7b64f4fd91e009abc8d904111d233e4399dc5fe4",
         intel: "c67824dbdfb94ed41151e7ec3823a77762bf227f17d3ffd4ae86fa2eb566a3aa"

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
