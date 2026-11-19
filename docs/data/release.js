// The newest release, for the Download page to fall back on when GitHub's API doesn't answer (it limits how often each
// visitor can ask). Update it with each release: the files' names and sizes are on the release's GitHub page.
window.VertexReleases = [
    {
        tag: "v0.1.0-pre-alpha",
        name: "Vertex 0.1.0-pre-alpha",
        url: "https://github.com/BiggyStudios/Vertex/releases/tag/v0.1.0-pre-alpha",
        date: "2026-11-19T02:18:52Z",
        pre: true,
        notes: "",
        assets: [
            { name: "Vertex-0.1.0-pre-alpha-win-x64.zip", size: 12648086 },
            { name: "Vertex-0.1.0-pre-alpha-linux-x64.tar.gz", size: 17865995 },
            { name: "Vertex-0.1.0-pre-alpha-osx-arm64.tar.gz", size: 15197784 },
            { name: "Vertex.Engine.0.1.0-pre-alpha.nupkg", size: 1014438 }
        ].map(a => ({ ...a, url: `https://github.com/BiggyStudios/Vertex/releases/download/v0.1.0-pre-alpha/${a.name}`, downloads: 0 }))
    }
];
