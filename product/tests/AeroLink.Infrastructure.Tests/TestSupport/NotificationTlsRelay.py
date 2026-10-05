import ssl, socket, sys, json, os
root, reply = sys.argv[1:3]
port = int(sys.argv[3]) if len(sys.argv) > 3 else 0
timeout = float(sys.argv[4]) if len(sys.argv) > 4 else 30
def fact(event):
    with open(os.path.join(root, 'events.jsonl'), 'a', encoding='utf-8') as evidence:
        evidence.write(json.dumps(dict(event=event)) + '\n'); evidence.flush(); os.fsync(evidence.fileno())
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(os.path.join(root, 'certificate.pem'), os.path.join(root, 'key.pem'))
listener = socket.socket(); listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); listener.bind(('127.0.0.1', port)); listener.listen(1); listener.settimeout(timeout)
print(listener.getsockname()[1], flush=True)
tls, data, wire, envelope = False, 0, '', ''
def send(s, value): s.sendall(value.encode('ascii'))
def line(s):
    value = bytearray()
    while True:
        b = s.recv(1)
        if not b: raise EOFError()
        if b == b'\n': return value.decode('ascii').rstrip('\r')
        value.extend(b)
        if len(value) > 65536: raise ValueError('fixture line bound')
try:
    with listener.accept()[0] as raw:
        fact('TcpAccepted'); raw.settimeout(timeout); send(raw, '220 owned fixture\r\n')
        assert line(raw).startswith('EHLO '); send(raw, '250-owned fixture\r\n250 STARTTLS\r\n')
        assert line(raw) == 'STARTTLS'; send(raw, '220 Ready\r\n'); tls = True; fact('TlsStarted')
        with context.wrap_socket(raw, server_side=True) as secure:
            assert line(secure).startswith('EHLO '); send(secure, '250 owned fixture\r\n')
            while True:
                command = line(secure)
                if command.startswith('MAIL FROM:'):
                    envelope += command + '\n'; send(secure, '250 Sender\r\n')
                elif command.startswith('RCPT TO:'):
                    envelope += command + '\n'
                    send(secure, '451 Later\r\n' if reply == 'Refuse451' else '550 Refused\r\n' if reply == 'Refuse550' else '250 Recipient\r\n')
                elif command == 'DATA':
                    send(secure, '354 Send\r\n'); parts = []
                    while True:
                        content = line(secure)
                        if content == '.': break
                        parts.append(content + '\r\n')
                    wire = ''.join(parts); data += 1; fact('DataReceived')
                    if reply == 'LoseFinalReply': break
                    send(secure, '250 Accepted\r\n'); fact('FinalDataAccepted')
                elif command == 'RSET': send(secure, '250 Reset\r\n')
                elif command == 'QUIT': print('QUIT', flush=True); break
                else: raise ValueError('unexpected command')
except (ssl.SSLError, OSError, EOFError): pass
finally:
    listener.close()
    print(json.dumps(dict(tls=tls, data=data, wire=wire, envelope=envelope)), flush=True)
