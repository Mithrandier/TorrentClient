﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace BitTorrentProtocol.Peer
{
    public class PeerTcpProtocol
    {
        public String LastError { get; private set; }
        private byte[] client_id;
        private TcpClient tcp_client;
        private byte[] partner_id;
        private Torrent torrent;
        private BitField peer_bitfield;
        private int current_piece_index;
        private int current_piece_offset;
        private List<byte> current_piece;

        public const String PROTOCOL_ID = "BitTorrent protocol";
        private const int FILE_BLOCK_SIZE = 16384;
        private const int PACKAGE_DEFAULT_SIZE = 1024;
        private const int HANDSHAKE_WAIT_TIMEOUT = 10000;
        private const int DEFAULT_DIALOG_WAIT_TIMEOUT = 4000;
        private const int UNCHOKE_WAIT_TIMEOUT = 20000;
        private const int PIECE_WAIT_TIMEOUT = 2000;
        private static Random random = new Random((int)DateTime.Now.Ticks);

        public PeerTcpProtocol(byte[] client_id)
        {
            this.client_id = client_id;
            this.current_piece = new List<byte>();
            return;
        }

        /// <summary>
        /// Download torrent file from specified peer
        /// </summary>
        /// <returns>true if succeeded</returns>
        public bool Connect(Torrent torrent, String peer_address)
        {
            this.torrent = torrent;
            try
            {
                CreateSocket(peer_address);
                Handshake();
                Dialog();
                return true;
            }
            catch (Exception ex)
            {
                if ((ex is SocketException) || (ex is WebException) || (ex is FormatException) || (ex is IOException) || (ex is ArgumentException))
                {
                    LastError = ex.Message;
                    return false;
                } else throw ex;
            }
            finally
            {
                if (tcp_client != null) tcp_client.Close();
            }
        }

        /// <summary>
        /// Create tcp socket for communication with peer
        /// </summary>
        /// <returns>true if succeeded</returns>
        protected void CreateSocket(String address)
        {
            String[] address_parts = address.Split(':');
            int port = Int32.Parse(address_parts[1]);
            String ip = address_parts[0];
            if (String.IsNullOrEmpty(ip)) throw new FormatException("Wrong peer address format");
            tcp_client = new TcpClient();
            IAsyncResult async = tcp_client.BeginConnect(ip, port, null, null);
            try
            {
                if (!async.AsyncWaitHandle.WaitOne(10*1000, false)) throw new SocketException();
                tcp_client.EndConnect(async);
            }
            finally
            {
                async.AsyncWaitHandle.Close();
            }
            return;
        }

        /// <summary>
        /// Perform handshake with specified peer
        /// </summary>
        /// <returns>true if succeeded</returns>
        protected void Handshake()
        {
            var stream = tcp_client.GetStream();            
            byte[] message = PeerTcpMessages.HandShake(torrent.InfoHash, client_id);
            stream.Write(message, 0, message.Length);
            byte[] response = new byte[68];
            tcp_client.Client.ReceiveTimeout = HANDSHAKE_WAIT_TIMEOUT;
            int result = stream.Read(response, 0, response.Length);
            if (result != 68) 
                throw new FormatException("Wrong peer response format");
            int pstrlen = response[0];
            String protocol = Encoding.UTF8.GetString(response, 1, pstrlen);
            byte[] info_hash = response.Skip(28).Take(20).Reverse().ToArray();
            partner_id = response.Skip(48).Take(20).ToArray();
            if (!(pstrlen == 19) || !protocol.Equals(PROTOCOL_ID) || !EqualBytes(info_hash, torrent.InfoHash))
                throw new FormatException("Wrong peer response format");
            return;
        }

        /// <summary>
        /// Dialog with another peer.
        /// </summary>
        protected void Dialog()
        {
            tcp_client.Client.ReceiveTimeout = DEFAULT_DIALOG_WAIT_TIMEOUT;
            var stream = tcp_client.GetStream(); 
            peer_bitfield = new BitField(torrent.PiecesCount);
            List<byte> pending_message = new List<byte>();
            bool pending = false;
            bool interesting = false;
            int current_length_to_read = 0;
            current_piece_offset = -1;
            while (true)
            {
                byte[] recv_buffer = new byte[PACKAGE_DEFAULT_SIZE];
                int read_bytes = 0;
                try
                {
                    read_bytes = stream.Read(recv_buffer, 0, recv_buffer.Length);
                    if (read_bytes == 0) throw new IOException();
                } 
                catch (IOException)
                {
                    if (interesting) break;
                    SendInterested();
                    interesting = true;
                    continue;
                }
                for (int i = 0; i < read_bytes; )
                {
                    /* Define amount of bytes to read */
                    if (!pending)
                    {
                        current_length_to_read = BigEndian.GetInt32(recv_buffer, i);
                        i += 4;
                        if (current_length_to_read == 0) break;
                    }
                    if (i + current_length_to_read > read_bytes)
                    { /* Message is not full */
                        int actial_length_to_read = read_bytes - i;
                        current_length_to_read = i + current_length_to_read - read_bytes;
                        byte[] message = recv_buffer.Skip(i).Take(actial_length_to_read).ToArray();
                        pending_message.AddRange(message);
                        pending = true;
                        break;
                    }
                    else
                    { /* Read and proccess message */
                        byte[] message = recv_buffer.Skip(i).Take(current_length_to_read).ToArray();
                        if (pending)
                        { /* Use pending data as prefix */
                            pending = false;
                            pending_message.AddRange(message);
                            message = pending_message.ToArray();
                            pending_message.Clear();
                        }
                        ProccessMessage(message);
                        i += current_length_to_read;

                        if (interesting)
                        {
                            int[] pieces_to_download = torrent.Bitfield.RequiredPieces(peer_bitfield);
                            if (pieces_to_download.Length == 0)
                                return; 
                        }
                    }
                }
            }
            return;
        }

        /// <summary>
        /// Process received message in dialog
        /// </summary>
        protected void ProccessMessage(byte[] message)
        {
            switch (message[0])
            {
                case (byte)PeerTcpMessages.ACTIONS.Have:
                    ParseHaveMessage(message);
                    break;
                case (byte)PeerTcpMessages.ACTIONS.Bitfield:
                    ParseBitfieldMessage(message);
                    break;
                case (byte)PeerTcpMessages.ACTIONS.Unchoke:
                    SendRequest();
                    break;
                case (byte)PeerTcpMessages.ACTIONS.Piece:
                    ProcessPiece(message);
                    SendRequest();
                    break;
                default:
                    break;
            }
            return;
        }

        /// <summary>
        /// Response as 'I want to download'
        /// </summary>
        protected void SendInterested()
        {
            var stream = tcp_client.GetStream();
            int[] pieces_to_download = torrent.Bitfield.RequiredPieces(peer_bitfield);
            byte[] message;
            if (pieces_to_download.Length > 0)
            {
                message = PeerTcpMessages.Interested();
                tcp_client.ReceiveTimeout = UNCHOKE_WAIT_TIMEOUT;
            }
            else
            {
                message = PeerTcpMessages.NotInterested();
            }
            stream.Write(message, 0, message.Length);
            return;
        }

        /// <summary>
        /// Response as 'Send me that data'
        /// </summary>
        protected void SendRequest()
        {
            if (peer_bitfield != null)
            {
                tcp_client.Client.ReceiveTimeout = PIECE_WAIT_TIMEOUT;
                int[] pieces_to_download = torrent.Bitfield.RequiredPieces(peer_bitfield);
                if (pieces_to_download.Length > 0)
                {
                    if (current_piece_offset == -1 || current_piece_offset > torrent.PieceLength(current_piece_index))
                    {
                        current_piece_index = pieces_to_download[random.Next(pieces_to_download.Length)];
                        current_piece_offset = 0;
                    }
                    byte[] message = PeerTcpMessages.Request(current_piece_index, current_piece_offset, BytesCountToRequest());
                    var stream = tcp_client.GetStream();
                    stream.Write(message, 0, message.Length);
                    current_piece_offset += FILE_BLOCK_SIZE;
                }
            }
            return;
        }

        /// <summary>
        /// Length of block to request.
        /// Usually differs from standart if the block is the last in its piece.
        /// </summary>
        protected int BytesCountToRequest()
        {
            if (current_piece_offset + FILE_BLOCK_SIZE <= torrent.PieceLength(current_piece_index))
                return FILE_BLOCK_SIZE;
            else
                return torrent.PieceLength(current_piece_index) - current_piece_offset;
        }

        /// <summary>
        /// Process received data
        /// </summary>
        protected void ProcessPiece(byte[] message)
        {
            Int32 piece_index = BigEndian.GetInt32(message, 1);
            Int32 block_index = BigEndian.GetInt32(message, 5);
            byte[] data = message.Skip(9).ToArray();
            current_piece.AddRange(data);
            if (block_index + data.Length >= torrent.PieceLength(piece_index))
            {
                lock (torrent)
                {
                    if (torrent.Bitfield.MissingPieces().Contains(piece_index))
                        torrent.SavePiece(current_piece.ToArray(), piece_index);
                }
                current_piece.Clear();
            }
            return;
        }

        /// <summary>
        /// Memorize info from Have message in peer's bitfield
        /// </summary>
        protected void ParseHaveMessage(byte[] message)
        {
            if (message.Length != 5) throw new FormatException("Wrong HAVE message received");
            int piece_index = BigEndian.GetInt32(message, 1);
            peer_bitfield.Set(piece_index);
            return;
        }

        /// <summary>
        /// Memorize peer's bitfield
        /// </summary>
        protected void ParseBitfieldMessage(byte[] message)
        {
            int field_length = message.Length - 1;
            peer_bitfield.Sum(message.Skip(1).ToArray());
            return;
        }
        
        /// <summary>
        /// Check equality of data arrays
        /// </summary>
        private bool EqualBytes(byte[] data1, byte[] data2)
        {
            if (data1.Length != data2.Length) return false;
            for (int i = 0; i < data1.Length;i++)
            {
                if (data1[i] != data2[i]) return false;
            }
            return true;
        }
    }
}
