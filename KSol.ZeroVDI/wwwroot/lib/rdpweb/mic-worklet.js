// mic-worklet.js — microphone capture on the audio rendering thread (AudioWorklet).
//
// Collects the mono input into ~43 ms blocks (2048 frames at 48 kHz) and posts each block to the main
// thread, which resamples it to the host's format and ships it (client.js _onMicBlock). Capture itself
// no longer depends on the main thread: the deprecated ScriptProcessorNode this replaces ran its
// callback there, so a busy main thread (decoding the remote desktop) dropped or stretched mic audio.

class RdpMicCapture extends AudioWorkletProcessor {
    constructor() {
        super();
        this._block = new Float32Array(2048);
        this._n = 0;
    }

    process(inputs) {
        const input = inputs[0] && inputs[0][0];
        // No input yet (device still starting): keep the node alive and wait.
        if (!input) return true;
        let i = 0;
        while (i < input.length) {
            const take = Math.min(input.length - i, this._block.length - this._n);
            this._block.set(input.subarray(i, i + take), this._n);
            this._n += take;
            i += take;
            if (this._n === this._block.length) {
                this.port.postMessage(this._block, [this._block.buffer]);
                this._block = new Float32Array(2048);
                this._n = 0;
            }
        }
        return true;
    }
}

registerProcessor("rdp-mic-capture", RdpMicCapture);
