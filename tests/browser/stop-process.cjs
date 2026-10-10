async function stopProcess(child, graceMs = 5000) {
    if (!child?.pid || child.exitCode !== null || child.signalCode !== null) return;
    await new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
            console.error('Test process did not stop after SIGTERM; sending SIGKILL.');
            child.kill('SIGKILL');
        }, graceMs);
        child.once('close', () => { clearTimeout(timer); resolve(); });
        child.once('error', error => { clearTimeout(timer); reject(error); });
        child.kill('SIGTERM');
    });
}
module.exports = {stopProcess};
