import AppKit
import SwiftUI

enum SettingsTab: Hashable { case providers, general, templates, about }

struct SettingsView: View {
    @ObservedObject var window: SettingsWindow

    var body: some View {
        TabView(selection: $window.tab) {
            ProvidersTab().tabItem { Label("AI Provider", systemImage: "sparkles") }.tag(SettingsTab.providers)
            GeneralTab().tabItem { Label("General", systemImage: "gearshape") }.tag(SettingsTab.general)
            TemplatesTab().tabItem { Label("Templates", systemImage: "text.badge.star") }.tag(SettingsTab.templates)
            AboutTab().tabItem { Label("About", systemImage: "info.circle") }.tag(SettingsTab.about)
        }
        .frame(width: 700, height: 640)
    }
}

// MARK: - General

private struct GeneralTab: View {
    @EnvironmentObject private var app: AppState
    @EnvironmentObject private var settings: SettingsStore

    var body: some View {
        Form {
            Section("Behaviour") {
                Toggle("Enable triggers", isOn: $settings.prefs.isEnabled)
                Toggle("Play sounds", isOn: $settings.prefs.playSounds)
                Toggle("Show floating indicator while working", isOn: $settings.prefs.showHUD)
                Toggle("Launch at login", isOn: $app.launchAtLogin)
            }

            Section("Permission") {
                HStack {
                    Image(systemName: app.hasAccessibility ? "checkmark.circle.fill" : "xmark.circle.fill")
                        .foregroundStyle(app.hasAccessibility ? .green : .red)
                        .contentTransition(.symbolEffect(.replace))
                    Text(app.hasAccessibility ? "Accessibility access granted" : "Accessibility access needed")
                    Spacer()
                    Button("Open Settings") { app.openAccessibilitySettings() }
                        .glassButton()
                }
            }
        }
        .formStyle(.grouped)
        .padding(10)
    }
}

// MARK: - Templates

struct TemplatesTab: View {
    @EnvironmentObject private var settings: SettingsStore
    @StateObject private var selectionState = ViewState<UUID?>(nil)

    private var selection: UUID? {
        get { selectionState.value }
        nonmutating set { selectionState.value = newValue }
    }

    var body: some View {
        HStack(spacing: 0) {
            VStack(spacing: 0) {
                List(selection: $selectionState.value) {
                    ForEach(settings.prefs.templates) { t in
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(t.name.isEmpty ? "Untitled" : t.name)
                                Text(t.trigger).font(.caption.monospaced()).foregroundStyle(.secondary)
                            }
                            Spacer()
                            if !t.isEnabled { Image(systemName: "pause.circle").foregroundStyle(.secondary) }
                        }
                        .tag(t.id)
                    }
                    .onMove { settings.prefs.templates.move(fromOffsets: $0, toOffset: $1) }
                }
                HStack(spacing: 0) {
                    Button { add() } label: { Image(systemName: "plus").frame(width: 24, height: 20) }
                    Button { remove() } label: { Image(systemName: "minus").frame(width: 24, height: 20) }
                        .disabled(selection == nil)
                    Spacer()
                    Button("Reset") {
                        settings.prefs.templates = Template.defaults
                        selection = nil
                    }
                    .font(.caption)
                }
                .buttonStyle(.borderless)
                .padding(6)
            }
            .frame(width: 180)
            .background(Color.primary.opacity(0.03))

            Divider()

            if let id = selection, settings.prefs.templates.contains(where: { $0.id == id }) {
                TemplateEditor(template: binding(for: id), isDuplicate: isDuplicate(id))
            } else {
                VStack(spacing: 8) {
                    Image(systemName: "text.cursor").font(.largeTitle).foregroundStyle(.tertiary)
                    Text("Select a template").foregroundStyle(.secondary)
                    Text("Type a trigger at the end of any text, in any app.")
                        .font(.caption).foregroundStyle(.tertiary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .padding(16)
    }

    private func binding(for id: UUID) -> Binding<Template> {
        Binding(
            get: { settings.prefs.templates.first { $0.id == id } ?? .placeholder },
            set: { new in
                if let i = settings.prefs.templates.firstIndex(where: { $0.id == id }) {
                    settings.prefs.templates[i] = new
                }
            }
        )
    }

    private func isDuplicate(_ id: UUID) -> Bool {
        guard let t = settings.prefs.templates.first(where: { $0.id == id }) else { return false }
        return settings.prefs.templates.contains { $0.id != id && $0.trigger == t.trigger && !t.trigger.isEmpty }
    }

    private func add() {
        let t = Template(name: "New template", trigger: "?new", prompt: "Describe what the AI should do with the text.")
        settings.prefs.templates.append(t)
        selection = t.id
    }

    private func remove() {
        guard let id = selection else { return }
        selection = nil
        settings.prefs.templates.removeAll { $0.id == id }
    }
}

private struct TemplateEditor: View {
    @Binding var template: Template
    let isDuplicate: Bool

    var body: some View {
        Form {
            TextField("Name", text: $template.name)
            TextField("Trigger", text: $template.trigger)
                .font(.body.monospaced())
            if isDuplicate {
                Text("Another template uses this trigger.").font(.caption).foregroundStyle(.red)
            }
            Picker("Result", selection: $template.mode) {
                ForEach(OutputMode.allCases) { Text($0.label).tag($0) }
            }
            Toggle("Enabled", isOn: $template.isEnabled)
            VStack(alignment: .leading, spacing: 6) {
                Text("Instruction for the AI")
                TextEditor(text: $template.prompt)
                    .font(.body)
                    .frame(minHeight: 110)
                    .scrollContentBackground(.hidden)
                    .padding(6)
                    .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 6))
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - About

private struct AboutTab: View {
    @EnvironmentObject private var usage: UsageStore
    @StateObject private var confirmReset = ViewState(false)

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    }

    var body: some View {
        VStack(spacing: 14) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .frame(width: 72, height: 72)
            Text("Inkwell").font(.title2.weight(.semibold))
            Text("Version \(version)").foregroundStyle(.secondary)
            Text("AI text triggers in every app. Your API keys stay in your Keychain; text goes only to the provider you choose.")
                .multilineTextAlignment(.center)
                .font(.callout)
                .foregroundStyle(.secondary)
                .frame(maxWidth: 380)

            VStack(spacing: 8) {
                Text("Made by Rahul Dev Banjara").font(.callout.weight(.medium))
                HStack(spacing: 10) {
                    AuthorLink(title: "LinkedIn", symbol: "person.crop.square",
                               url: "https://www.linkedin.com/in/devrahulbanjara/")
                    AuthorLink(title: "Website", symbol: "globe",
                               url: "https://rahuldevbanjara.com.np")
                    AuthorLink(title: "GitHub", symbol: "chevron.left.forwardslash.chevron.right",
                               url: "https://github.com/devrahulbanjara")
                }
            }
            .padding(14)
            .glassCard(cornerRadius: 16)
            .padding(.top, 4)

            HStack {
                Button("Open data folder") { NSWorkspace.shared.open(usage.folder) }
                Button("Reset statistics…", role: .destructive) { confirmReset.value = true }
            }
            .padding(.top, 6)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .confirmationDialog("Delete all usage statistics?", isPresented: $confirmReset.value) {
            Button("Delete", role: .destructive) { usage.reset() }
        }
    }
}

private struct AuthorLink: View {
    let title: String
    let symbol: String
    let url: String

    var body: some View {
        Button {
            if let u = URL(string: url) { NSWorkspace.shared.open(u) }
        } label: {
            Label(title, systemImage: symbol)
        }
        .glassButton()
        .help(url)
    }
}
