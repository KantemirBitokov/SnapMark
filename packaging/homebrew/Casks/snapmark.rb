cask "snapmark" do
  arch arm: "arm64", intel: "x64"

  version "1.9.24"
  sha256 arm:   "b6ddd6fb4e1981a87d45622f5e3dd4109a90d1465e21f47d671ab5eeadda0cc8",
         intel: "8c3baaac66c88c00162b5885c4b7db8656ed8903fae2f773655e4f7ddfd82a35"

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
