const { Client } = require('ssh2');
const conn = new Client();
conn.on('ready', () => {
    conn.exec("systemctl status nginx", (err, stream) => {
        if (err) throw err;
        stream.on('close', () => conn.end()).on('data', (data) => console.log(data.toString()));
    });
}).connect({host: "95.182.87.6", port: 22, username: "root", password: "q4xCC=SQGjgpNqgv7Ot"});
