/*
Copyright (C) 2005-2007  P. Oscar Boykin <boykin@pobox.com>, University of Florida

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

//#define DEBUG

using System.Collections;
using System.Threading;
using System;
#if BRUNET_NUNIT
using NUnit.Framework;
#endif

namespace Brunet
{
/**
 * Channel something that allows you to Enqueue objects and Dequeue
 * objects in a thread-safe way.  It has EnqueueEvent and CloseEvent
 * which signal when something has been added, or when the Channel
 * is closed.  All operations are non-blocking.  @see BlockingQueue
 * for a channel that can block until something is ready to be read
 * from it
 */
public class Channel {
  /**
   * Create a new channel with an unlimited number of allowed enqueues
   */
  public Channel() : this(-1) { }
  /**
   * @param max_enqueues the maximum number of times Enqueue is allowed, after
   * that it will throw InvalidOperationException and the queue will be closed
   */
  public Channel(int max_enqueues) {
    if( max_enqueues == 0 ) {
      //This doesn't make sense
      throw new ArgumentOutOfRangeException("max_enqueues", max_enqueues, "cannot be zero");
    }
    _closed = false;
    _sync = new object();
    _queue = new Queue();
    _close_on_enqueue = max_enqueues;
    _close_event = new FireOnceEvent();
  }

  protected readonly Queue _queue;
  protected readonly object _sync; 
  protected bool _closed;
  /*
   * If this is less than zero, allow an infinite number of enqueues.
   * If this is greater than or equal to zero, allow that many enqueues
   * before calling Close.
   */
  protected int _close_on_enqueue;
  
  public bool Closed { get { lock ( _sync ) { return _closed; } } }
  
  public int Count { get { lock ( _sync ) { return _queue.Count; } } }
 
  /**
   * When an item is enqueued, this event is fire
   */
  public event EventHandler EnqueueEvent;
  private FireOnceEvent _close_event;
  /**
   * When the queue is closed, this event is fired
   * If the CloseEvent has already been fired, registering
   * to this event throws an Exception
   */
  public event EventHandler CloseEvent {
    add { _close_event.Add(value); }
    remove { _close_event.Remove(value); }
  }
  
  /* **********************************************
   * Here all the methods
   */
  
  /**
   * Once this method is called, and the queue is emptied,
   * all future Dequeue's will throw exceptions
   */
  public virtual void Close() {
    lock( _sync ) { _closed = true; }
    _close_event.Fire(this, null);
#if DEBUG
    Console.Error.WriteLine("Close set");
#endif
  }

  /**
   * After the next enqueue, close
   * If the queue is already closed, this does nothing.
   * This is totally equivalent to listening to the
   * EnqueueEvent and calling Close on the queue after
   * an Enqueue.
   * @return Closed
   * @deprecated use the constructor to specify 1 as the max number of
   * enqueues
   */
  public bool CloseAfterEnqueue() {
    lock( _sync ) {
      /*
       * If we are not closed or about to call close
       */
      if( !_closed && (_close_on_enqueue != 0) ) {
        _close_on_enqueue = 1;
        return false;
      }
      else {
        return true;
      }
    }
  }
  
  /**
   * @throw InvalidOperationException if Closed or Empty
   */
  public virtual object Dequeue() {
    lock( _sync ) {
      return _queue.Dequeue();
    }
  }

  /**
   * add a to the queue.
   * @throws Exception if the Queue is closed
   * @return Count after this Enqueue (before any Dequeue has a chance to act)
   */
  public virtual int Enqueue(object a) {
    bool close = false;
    int count = -1;
    lock( _sync ) {
      if( !_closed && (_close_on_enqueue != 0) ) {
        _queue.Enqueue(a);
        count = _queue.Count;
        if( _close_on_enqueue > 0 ) {
          //Decrement the number of allowed enqueues:
          --_close_on_enqueue;
          close = (_close_on_enqueue == 0);
        }
      }
      else {
        //We are closed, ignore all future enqueues.
        throw new InvalidOperationException("Channel is closed, Enqueue failed");
      }
    }
    FireEnqueue();
    if( close ) {
      Close();  
    }
    return count;
  }
  protected void FireEnqueue() {
    //the event:
    EventHandler eh = EnqueueEvent;
    if( eh != null ) {
      eh(this, EventArgs.Empty);
    }
  }
  /**
   * @throw Exception if the queue is closed
   */
  public virtual object Peek() {
    lock( _sync ) {
      return _queue.Peek();
    }
  }

}

/**
 * This class offers a means to pass objects in a queue
 * between threads (or in the same thread).  The Dequeue
 * method will block until there is something in the Queue
 *
 * This implementation uses two thread synchronization tools:
 * mutex (locking) and a WaitHandle.  The WaitHandle is in the
 * Set state when there are 1 or more items in the queue.
 * When there are 1 or more items, Enqueue can't change the "Set"
 * state, so there is no need to call Set.  When there are two
 * or more items, Dequeue can't change the set state.  This
 * observation makes the BlockingQueue much faster because in the
 * high throughput case, there is often more than 1 item in the queue,
 * and so we only use the Mutex and never touch the WaitHandle,
 * which can be a little slow (according to testing).
 */
#if BRUNET_NUNIT
[TestFixture]
#endif
public sealed class BlockingQueue : Channel {
  
  /**
   * Create a BlockingQueue with an unlimited number of allowed Enqueues
   */
  public BlockingQueue() : this(-1) { }

  /**
   * Create a BlockingQueue with a limited number of allowed Enqueues
   * @param max_enqueues, the maximum number of allowed Enqueues before we
   * Close the queue and start throwing exceptions.
   */
  public BlockingQueue(int max_enqueues) : base(max_enqueues) {
    _waiters = 0;
    _re = new AutoResetEvent(false); 
    _re_sync = new object();
  }

  ~BlockingQueue() {
    //Make sure the close method is eventually called:
    Close();
  }
  protected AutoResetEvent _re;
  protected readonly object _re_sync; 
  protected int _waiters;

  /* **********************************************
   * Here all the methods
   */
  
  /**
   * Once this method is called, and the queue is emptied,
   * all future Dequeue's will throw exceptions
   */
  public override void Close() {
    bool close_are = false;
    lock( _sync ) {
      _closed = true;
      /*
       * If there is no one waiting, go ahead and
       * close the re.
       *
       * If there are waiters, then
       * we Set the re, so everyone can see that
       * the queue is closed.
       */
      close_are = (_waiters == 0);
    }
    //Fire the close event
    base.Close();
    
    //We either set it, or close it:
    SetOrCloseRE(!close_are, close_are);
#if DEBUG
    System.Console.Error.WriteLine("Close set");
#endif
  }

  /** Atomically interact with the RE, return true if it was closed BEFORE
   * @param set if true, try to Set the RE
   * @param close if true, try to Close the RE
   * @return true if the RE was already closed.
   * both arguments should not be true
   */
  protected bool SetOrCloseRE(bool set, bool close) {
    lock( _re_sync ) {
      if( _re == null ) {
        return true;
      }
      //Looks like we can actually try to work now:
      if( set ) {
        //This is the more common case, so put it first
        _re.Set();
      }
      else if( close ) {
        _re.Close();
        _re = null;
      }
    }
    //If we got here, it wasn't already closed
    return false;
  }
  /**
   * @throw Exception if the queue is closed
   */
  public override object Dequeue() {
    bool timedout = false;
    return Dequeue(-1, out timedout);
  }

  /**
   * @param millisec how many milliseconds to wait if the queue is empty
   * @param timedout true if we have to wait too long to get an object
   * @return the object, if we timeout we return null
   * @throw Exception if the BlockingQueue is closed and empty
   */
  public object Dequeue(int millisec, out bool timedout) {
    return Dequeue(millisec, out timedout, true);
  }
  
  protected object Dequeue(int millisec, out bool timedout, bool advance)
  {
    lock( _sync ) {
      if( (_queue.Count > 1) || _closed ) {
        /**
         * If _queue.Count == 1, the Dequeue may return us to the empty
         * state, which we always handled below
         */
        timedout = false;
        if( advance ) {
          return _queue.Dequeue();
        }
        else {
          return _queue.Peek();
        }
      }
      _waiters++;
    }
    //Wait for the next one... 
    try {
      timedout = !_re.WaitOne(millisec, false);
    } catch (NullReferenceException) {
      timedout = false;
      //We could get a nullReferenceException if _re is null,
      //which happens in the case that it has already been closed.
    } 
    bool set = false;
    bool are_close = false;
    object result = null;
    try {
      lock( _sync ) {
        //If we are closed, we should stay set
        set = _closed;
        _waiters--;
        /* 
         * No matter what, if the queue is closed
         * and we are the last _waiter, then go
         * ahead and close the _re.
         */
        are_close = _closed && (_waiters == 0);
        if( !timedout ) {
          if( advance ) {
            result = _queue.Dequeue();
          }
          else {
            result = _queue.Peek();
          }
          /*
           * If there is
           * still more, set the _re so the
           * next reader can get it
           */
          set |= (_queue.Count > 0);
        }
      }
    }
    finally {
      /*
       * We are about to leave, let's see if we need to
       * set or close the _re.  If we have to close,
       * we never need to set.
       *
       * The case where we are closed, the queue should be set,
       * but we may also be able to go ahead and close.
       * We have to do it in this order, or we may either needlessly
       * Set then Close, or Close before we called Set.
       */
      SetOrCloseRE(set, are_close);
    }
    return result;
  }

  /**
   * @throw Exception if the queue is closed
   */
  public override object Peek() {
    bool timedout = false;
    return Peek(-1, out timedout);
  }

  /**
   * @param millisec how many milliseconds to wait if the queue is empty
   * @param timedout true if we have to wait too long to get an object
   * @return the object, if we timeout we return null
   * @throw Exception if the BlockingQueue is closed and empty
   */
  public object Peek(int millisec, out bool timedout)
  {
    return Dequeue(millisec, out timedout, false);
  }

  /**
   * add a to the queue.
   * @throws Exception if the Queue is closed
   * @return Count after this Enqueue (before any Dequeue has a chance to act)
   */
  public override int Enqueue(object a) {
    bool close = false;
    int count = -1;
    lock( _sync ) {
      if( !_closed && (_close_on_enqueue != 0) ) {
        _queue.Enqueue(a);
        count = _queue.Count;
        if( _close_on_enqueue > 0 ) {
          //Decrement the number of allowed enqueues:
          --_close_on_enqueue;
          close = (_close_on_enqueue == 0);
        }
      }
      else {
        //We are closed, ignore all future enqueues.
        throw new InvalidOperationException("BlockingQueue is closed, Enqueue failed");
      }
      //Wake up any waiting threads
#if DEBUG
      Console.Error.WriteLine("Enqueue set: count {0}", Count);
#endif
    }
    //If we just went from 0 -> 1 signal any waiting Dequeue
    if( count == 1 ) { 
      SetOrCloseRE(true, false);
    }
    //After we have alerted any blocking threads (Set), fire
    //the event:
    FireEnqueue();
    if( close ) {
      Close();  
    }
    return count;
  }

  /**
   * This method is not defined if there are other Dequeues pending
   * on any of these Queues.  REPEAT: if you are using Select you
   * cannot be doing Dequeues in another thread on any of these Queues.
   *
   * Tests seem to show that mono has a problem if there are more than 64
   * queues that we are waiting on, so this can't scale to huge numbers.
   *
   * @param queues a list of non-null BlockingQueue objects.
   * @param timeout how long to wait in milliseconds
   * @return the index into the list of a queue that is ready to Dequeue, -1 there is a timeout
   */
  public static int Select(IList queues, int timeout) {
    WaitHandle[] wait_handles = new WaitHandle[ queues.Count ];
    int idx = 0; 
    foreach (BlockingQueue q in queues) {
       wait_handles[idx] = q._re;
       idx++;
    }
    idx = WaitHandle.WaitAny(wait_handles, timeout, true);
    if (idx == WaitHandle.WaitTimeout) {
      return -1;
    }
    //Reset the AutoResetEvent
    BlockingQueue t = (BlockingQueue)queues[idx];
    t._re.Set();
    return idx;
  }

#if BRUNET_NUNIT
  public void TestThread1()
  {
    //See a random number generator with the number 1.
    Random r = new Random(1);
    for(int i = 0; i < 100000; i++) { 
      Enqueue( r.Next() );
    }
    Close();
  }
  
  [Test]
  public void TestThread2()
  {
    Thread t = new Thread(this.TestThread1);
    t.Start();
    Random r = new Random(1);
    for(int i = 0; i < 100000; i++) { 
      Assert.AreEqual( Dequeue(), r.Next(), "dequeue equality test" );
    }
//    Console.Error.WriteLine("Trying to get an exception");
    //The next dequeue should throw an exception
    bool got_exception = false;
    try {
      Dequeue();
    }
    catch(Exception) { got_exception = true; }
    Assert.IsTrue(got_exception, "got exception");
    //Try it again
    got_exception = false;
    try {
      Dequeue();
    }
    catch(Exception) { got_exception = true; }
    Assert.IsTrue(got_exception, "got exception");
    Close();
    got_exception = false;
    try {
      CloseEvent += delegate(object o, EventArgs args) { };
    }
    catch { got_exception = true; }
    Assert.IsTrue(got_exception, "CloseEvent exception check");
    
    got_exception = false;
    try {
      //Should throw an exception after the queue is closed
      Enqueue(null);
    }
    catch { got_exception = true; }
    Assert.IsTrue(got_exception, "Enqueue exception check");
    
  }

  /*
   * Used to test the select method
   */
  protected class SelectTester {
    protected IList _queues;
    protected const int TRIALS = 50000;
    public SelectTester(IList queues) {
      _queues = queues;
    }

    public void StartEnqueues() {
      Random q_r = new Random();
      for(int i = 0; i < TRIALS; i++) {
        int idx = q_r.Next(0, _queues.Count);
        BlockingQueue q = (BlockingQueue)_queues[ idx ];
	q.Enqueue( idx );
      }
    }

    public void CheckQueues() {
      for(int i = 0; i < TRIALS; i++) {
        int idx = BlockingQueue.Select( _queues, 5000 );
	Assert.IsFalse( idx == -1, "Timeout check");
	BlockingQueue b = (BlockingQueue)_queues[idx];
	bool timedout;
	object val = b.Dequeue(0, out timedout);
	Assert.IsFalse(timedout, "Dequeue didn't time out");
        Assert.AreEqual(val, idx, "Dequeue matches index");
      }
      //Any future selects *should* timeout
      int idx2 = BlockingQueue.Select( _queues, 500 );
      Assert.AreEqual( idx2, -1, "Did timeout");
    }
  }

  [Test]
  public void SelectTest() {
    ArrayList l = new ArrayList();
    for(int i = 0; i < 64; i++) {
      l.Add( new BlockingQueue() );
    }
    SelectTester test = new SelectTester(l);
    Thread t = new Thread( test.StartEnqueues );
    t.Start();
    test.CheckQueues();
    foreach(BlockingQueue q in l) {
      q.Close();
    }
  }

  protected class WriterState {
    protected readonly BlockingQueue _q;
    protected readonly ArrayList _list;
    protected readonly int _runs;

    public WriterState(BlockingQueue q, ArrayList all, int runs) {
      _q = q;
      _list = all;
      _runs = runs;
    }
    public void Start() {
      Random r = new Random();
      for(int i = 0; i < _runs; i++) {
        int rn = r.Next();
        _q.Enqueue(rn);
        lock( _list ) {
          _list.Add( rn );
        }
      }
    }
  }
  protected class ReaderState {
    protected readonly BlockingQueue _q;
    protected readonly ArrayList _list;

    public ReaderState(BlockingQueue q, ArrayList all) {
      _q = q;
      _list = all;
    }
    public void Start() {
      try {
        while(true) {
          object o = _q.Dequeue();
          lock( _list ) { _list.Add( o ); }
        }
      }
      catch(InvalidOperationException) {
        //Queue is closed now.
      }
    }
  }

  [Test]
  public void MultipleWriterTest() {
    const int WRITERS = 5;
    const int READERS = 5;
    const int writes = 10000;
    ArrayList written_list = new ArrayList();
    ArrayList read_list = new ArrayList();
    ArrayList write_threads = new ArrayList();
    ArrayList read_threads = new ArrayList();
    BlockingQueue q = new BlockingQueue();

    /* Start the writers */
    for( int i = 0; i < WRITERS; i++ ) {
      WriterState ws = new WriterState(q, written_list, writes);
      Thread t = new Thread( ws.Start );
      write_threads.Add( t );
      t.Start();
    }
    /* Start the readers */
    for( int i = 0; i < READERS; i++) {
      ReaderState rs = new ReaderState(q, read_list);
      Thread t = new Thread( rs.Start );
      read_threads.Add( t );
      t.Start();
    }
    foreach(Thread t in write_threads) {
      t.Join();
    }
    //Writing is done, close the queue, and join the readers:
    q.Close();
    foreach(Thread t in read_threads) {
      t.Join();
    }

    //Check that the reader list is the same as the written list:
    ArrayList read_copy = new ArrayList(read_list);
    ArrayList write_copy = new ArrayList(written_list);
    //Remove all the reads from the written copy:
    foreach(object o in read_list) {
      int i = write_copy.IndexOf(o);
      Assert.IsTrue( i >= 0, "read something not in written");
      write_copy.RemoveAt(i);
    }
    Assert.IsTrue( write_copy.Count == 0, "More written than read");
    //Remove all the writes from the read copy:
    foreach(object o in written_list) {
      int i = read_copy.IndexOf(o);
      Assert.IsTrue( i >= 0, "wrote something not in read");
      read_copy.RemoveAt(i);
    }
    Assert.IsTrue( read_copy.Count == 0, "More written than read");
  }

  [Test]
  public void MaxEnqueueTest() {
    Exception cx = null;
    BlockingQueue q = null;
    try {
      q = new BlockingQueue(0);
      Assert.IsFalse(true, "didn't throw exception on create 0");
    }
    catch(ArgumentOutOfRangeException x) {
      cx = x;
    }
    Assert.IsNotNull(cx, "max_enqueue 0 caused exception");
    q = new BlockingQueue(3);
    Assert.AreEqual(1, q.Enqueue("hey"), "enqueue 1");
    Assert.AreEqual(2, q.Enqueue("hey"), "enqueue 1");
    Assert.AreEqual(3, q.Enqueue("hey"), "enqueue 1");
    cx = null;
    try {
      q.Enqueue("oh no!");
      Assert.IsFalse(true, "didn't throw exception on enqueue 4");
    }
    catch(InvalidOperationException iox) {
      cx = iox;
    }
    Assert.IsNotNull(cx, "4th enqueue caused a problem");
  }
#endif
}

}
