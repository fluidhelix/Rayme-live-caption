# Speech fixture provenance

The test fetches `samples/jfk.wav` from the exact whisper.cpp commit in `assets.json` and verifies its SHA-256. It is the excerpt “And so, my fellow Americans: ask not what your country can do for you — ask what you can do for your country” from President John F. Kennedy's January 20, 1961 inaugural address, a public-domain United States federal government speech/recording (17 USC §105).

Historical provenance: [JFK Presidential Library inaugural address](https://www.jfklibrary.org/learn/about-jfk/historic-speeches/inaugural-address). Distribution source: [whisper.cpp pinned sample](https://github.com/ggml-org/whisper.cpp/blob/d1be6fde11ac6e0407606b4e42fe72d34add8037/samples/jfk.wav). The software repository is MIT licensed; the recording's public-domain provenance, rather than the software license alone, is the basis for using it here.

The WAV is downloaded into ignored test artifacts, not the production Whisper directory. No private conversation recordings are used. Synthetic tone producers generate original test signals in memory. The CI-only tiny.en model is an MIT-licensed Whisper conversion distributed by the whisper.cpp project; its published SHA-1 is pinned from the same upstream commit's `models/README.md`. It never replaces or copies the user's production model.
