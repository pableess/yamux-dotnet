package main

import (
	"fmt"
	"io"
	"net"
	"os"

	"github.com/hashicorp/yamux"
)

func main() {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		fmt.Fprintf(os.Stderr, "listen error: %v\n", err)
		os.Exit(1)
	}

	port := listener.Addr().(*net.TCPAddr).Port
	fmt.Printf("LISTENING:%d\n", port)
	os.Stdout.Sync()

	conn, err := listener.Accept()
	if err != nil {
		fmt.Fprintf(os.Stderr, "accept error: %v\n", err)
		os.Exit(1)
	}

	config := yamux.DefaultConfig()
	config.LogOutput = io.Discard
	config.EnableKeepAlive = false
	session, err := yamux.Server(conn, config)
	if err != nil {
		fmt.Fprintf(os.Stderr, "yamux server error: %v\n", err)
		os.Exit(1)
	}

	for {
		stream, err := session.AcceptStream()
		if err != nil {
			break
		}
		go func() {
			io.CopyBuffer(io.Discard, stream, make([]byte, 64*1024))
			stream.Close()
		}()
	}

	os.Exit(0)
}