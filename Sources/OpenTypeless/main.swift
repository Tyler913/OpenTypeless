import AppKit

// `OpenTypeless --transcribe-file <audio>` runs the full STT + polish pipeline on a file and prints
// the result, without the UI. Useful for testing long recordings end to end.
if let index = CommandLine.arguments.firstIndex(of: "--transcribe-file"),
   CommandLine.arguments.count > index + 1 {
    let path = CommandLine.arguments[index + 1]
    let polish = !CommandLine.arguments.contains("--no-polish")
    let realtime = CommandLine.arguments.contains("--realtime")
    Task {
        let code = await CLI.transcribe(path: path, polish: polish, realtime: realtime)
        exit(code)
    }
    dispatchMain()
}

if CommandLine.arguments.contains("--eval-polish") {
    Task { exit(await PolishEval.run()) }
    dispatchMain()
}

let app = NSApplication.shared

if let index = CommandLine.arguments.firstIndex(of: "--snapshot-ui"), CommandLine.arguments.count > index + 1 {
    app.setActivationPolicy(.accessory)
    MainActor.assumeIsolated { UISnapshots.run(outputDirectory: CommandLine.arguments[index + 1]) }
    exit(0)
}

app.setActivationPolicy(.accessory)
let delegate = MainActor.assumeIsolated { AppDelegate() }
app.delegate = delegate
app.run()
