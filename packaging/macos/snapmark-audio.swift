// snapmark-audio — запись звука компьютера на macOS 13+ через ScreenCaptureKit (без драйверов).
//
// Использование:  snapmark-audio <файл.pcm>
//   stdout: «STARTED» — захват запущен; «FORMAT f32le <частота> <каналы>» — формат файла.
//   stdin:  «arm»  — начать писать (в момент старта видео), «stop» или конец ввода — завершить.
// Файл — сырой PCM float32, каналы чередуются. Паузы (когда ничего не играет) заполняются тишиной,
// чтобы длина звука совпадала с реальным временем и с видео.
//
// Собирается в build-macos.sh:  swiftc -O -target arm64-apple-macos13 snapmark-audio.swift -o snapmark-audio

import Foundation
import ScreenCaptureKit
import CoreMedia
import AVFoundation

@available(macOS 13.0, *)
final class SystemAudioRecorder: NSObject, SCStreamOutput, SCStreamDelegate {
    private let handle: FileHandle
    private let lock = NSLock()
    private var stream: SCStream?
    private var armed = false
    private var armTime: Date?
    private var framesWritten: Int64 = 0
    private var sampleRate: Double = 48000
    private var channels = 2
    private var announced = false

    init(path: String) {
        FileManager.default.createFile(atPath: path, contents: nil)
        guard let h = FileHandle(forWritingAtPath: path) else {
            fputs("ERROR cannot open \(path)\n", stderr)
            exit(1)
        }
        handle = h
    }

    func start() async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else {
            throw NSError(domain: "snapmark", code: 1, userInfo: [NSLocalizedDescriptionKey: "no display"])
        }
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let config = SCStreamConfiguration()
        config.capturesAudio = true
        config.excludesCurrentProcessAudio = true
        config.sampleRate = 48000
        config.channelCount = 2
        // Видео нам не нужно — минимальный размер и частота, чтобы не тратить ресурсы.
        config.width = 2
        config.height = 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 1)

        let s = SCStream(filter: filter, configuration: config, delegate: self)
        try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: DispatchQueue(label: "snapmark.audio"))
        try await s.startCapture()
        stream = s
        print("STARTED")
        fflush(stdout)
    }

    func arm() {
        lock.lock(); defer { lock.unlock() }
        if armed { return }
        armed = true
        armTime = Date()
        framesWritten = 0
    }

    /// Дописать тишину до текущего момента и закрыть файл.
    func finish() {
        lock.lock()
        if armed { padToNow(extra: 0) }
        armed = false
        try? handle.synchronize()
        try? handle.close()
        lock.unlock()
        if let s = stream {
            let done = DispatchSemaphore(value: 0)
            s.stopCapture { _ in done.signal() }
            _ = done.wait(timeout: .now() + 2)
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sampleBuffer.isValid,
              let desc = sampleBuffer.formatDescription,
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(desc)?.pointee else { return }

        let frames = sampleBuffer.numSamples
        let ch = Int(asbd.mChannelsPerFrame)
        let isFloat = asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let nonInterleaved = asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        guard isFloat, asbd.mBitsPerChannel == 32, ch > 0, frames > 0 else { return }

        lock.lock(); defer { lock.unlock() }
        if !announced {
            sampleRate = asbd.mSampleRate
            channels = ch
            print("FORMAT f32le \(Int(sampleRate)) \(channels)")
            fflush(stdout)
            announced = true
        }
        guard armed else { return }

        padToNow(extra: frames)
        try? sampleBuffer.withAudioBufferList { list, _ in
            if nonInterleaved {
                var out = [Float](repeating: 0, count: frames * ch)
                for c in 0..<min(ch, list.count) {
                    guard let p = list[c].mData?.assumingMemoryBound(to: Float.self) else { continue }
                    for i in 0..<frames { out[i * ch + c] = p[i] }
                }
                out.withUnsafeBytes { handle.write(Data($0)) }
            } else {
                for b in list {
                    if let d = b.mData { handle.write(Data(bytes: d, count: Int(b.mDataByteSize))) }
                }
            }
        }
        framesWritten += Int64(frames)
    }

    /// Если отстаём от реального времени больше чем на 150 мс — дописываем тишину.
    private func padToNow(extra: Int) {
        guard let start = armTime else { return }
        let expected = Int64(Date().timeIntervalSince(start) * sampleRate) - Int64(extra)
        var lag = expected - framesWritten
        if Double(lag) < sampleRate * 0.15 { return }
        let chunk = 4800
        let zeros = Data(count: chunk * channels * 4)
        while lag > 0 {
            let n = Int(min(lag, Int64(chunk)))
            handle.write(zeros.prefix(n * channels * 4))
            framesWritten += Int64(n)
            lag -= Int64(n)
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        fputs("ERROR \(error.localizedDescription)\n", stderr)
    }
}

guard CommandLine.arguments.count >= 2 else {
    fputs("usage: snapmark-audio <output.pcm>\n", stderr)
    exit(2)
}

if #available(macOS 13.0, *) {
    let recorder = SystemAudioRecorder(path: CommandLine.arguments[1])
    Task {
        do { try await recorder.start() }
        catch {
            fputs("ERROR \(error.localizedDescription)\n", stderr)
            exit(1)
        }
    }
    Thread {
        while let line = readLine() {
            let cmd = line.trimmingCharacters(in: .whitespaces)
            if cmd == "arm" { recorder.arm() }
            else if cmd == "stop" { break }
        }
        recorder.finish()
        exit(0)
    }.start()
    RunLoop.main.run()
} else {
    fputs("ERROR macOS 13 or newer is required\n", stderr)
    exit(3)
}
